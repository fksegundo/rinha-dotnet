use std::io;
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::net::UnixStream;
use std::thread;
use std::time::Duration;

const PORT: u16 = 9999;
const UPSTREAMS: [&str; 2] = ["/sockets/api1.sock", "/sockets/api2.sock"];
const BACKLOG: i32 = 65_535;
const MAX_EVENTS: i32 = 256;
const ACCEPT_BATCH_LIMIT: u32 = 64;
const WARMUP_ATTEMPTS: u32 = 300;
const WARMUP_INTERVAL: Duration = Duration::from_millis(10);
const RESPONSE_NOT_READY: &[u8] = b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n";
const CONTROL_SNDBUF: i32 = 256 * 1024;

struct ControlChannel {
    stream: UnixStream,
    byte: u8,
    control: [u8; 64],
    cmsg_len: usize,
}

impl ControlChannel {
    fn send_fd(&mut self, fd_to_send: RawFd) -> io::Result<()> {
        let mut iov = libc::iovec {
            iov_base: (&mut self.byte as *mut u8).cast(),
            iov_len: 1,
        };
        unsafe {
            let data = self
                .control
                .as_mut_ptr()
                .add(std::mem::size_of::<libc::cmsghdr>())
                .cast::<RawFd>();
            *data = fd_to_send;

            let msg = libc::msghdr {
                msg_name: std::ptr::null_mut(),
                msg_namelen: 0,
                msg_iov: &mut iov,
                msg_iovlen: 1,
                msg_control: self.control.as_mut_ptr().cast(),
                msg_controllen: self.cmsg_len,
                msg_flags: 0,
            };

            let sent = libc::sendmsg(self.stream.as_raw_fd(), &msg, libc::MSG_NOSIGNAL);
            if sent != 1 {
                return Err(io::Error::last_os_error());
            }
        }
        Ok(())
    }
}

fn main() {
    ignore_sigpipe();

    let listener = make_listener(PORT).expect("failed to bind listener");
    let listener_fd = listener.as_raw_fd();
    set_nonblocking(listener_fd).expect("failed to set listener non-blocking");

    let mut controls: [Option<ControlChannel>; 2] = [None, None];
    warmup_controls(&mut controls);

    let epoll_fd = unsafe { libc::epoll_create1(libc::EPOLL_CLOEXEC) };
    if epoll_fd < 0 {
        panic!("epoll_create1 failed: {}", io::Error::last_os_error());
    }

    let mut event = libc::epoll_event {
        events: libc::EPOLLIN as u32,
        u64: listener_fd as u64,
    };

    if unsafe { libc::epoll_ctl(epoll_fd, libc::EPOLL_CTL_ADD, listener_fd, &mut event) } != 0 {
        panic!("epoll_ctl failed: {}", io::Error::last_os_error());
    }

    let mut upstream_idx = 0usize;
    let mut reconnect_needed = [false; 2];
    let mut events = [libc::epoll_event { events: 0, u64: 0 }; MAX_EVENTS as usize];

    loop {
        reconnect_controls(&mut controls, &mut reconnect_needed);

        let ready = unsafe { libc::epoll_wait(epoll_fd, events.as_mut_ptr(), MAX_EVENTS, -1) };
        if ready < 0 {
            let err = io::Error::last_os_error();
            if err.raw_os_error() == Some(libc::EINTR) {
                continue;
            }
            eprintln!("epoll_wait error: {err}");
            break;
        }

        for i in 0..ready as usize {
            if events[i].u64 as RawFd != listener_fd {
                continue;
            }

            accept_burst(
                listener_fd,
                &mut upstream_idx,
                &mut controls,
                &mut reconnect_needed,
            );
        }
    }
}

fn accept_burst(
    listener_fd: RawFd,
    upstream_idx: &mut usize,
    controls: &mut [Option<ControlChannel>; 2],
    reconnect_needed: &mut [bool; 2],
) {
    for _ in 0..ACCEPT_BATCH_LIMIT {
        let client_fd = unsafe {
            libc::accept4(
                listener_fd,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                libc::SOCK_CLOEXEC | libc::SOCK_NONBLOCK,
            )
        };

        if client_fd < 0 {
            let err = io::Error::last_os_error();
            if err.raw_os_error() == Some(libc::EAGAIN) {
                return;
            }
            if err.raw_os_error() == Some(libc::EINTR) {
                continue;
            }
            eprintln!("accept error: {err}");
            return;
        }

        configure_tcp(client_fd);

        if !handoff(client_fd, *upstream_idx, controls, reconnect_needed) {
            let _ = unsafe {
                libc::write(
                    client_fd,
                    RESPONSE_NOT_READY.as_ptr().cast(),
                    RESPONSE_NOT_READY.len(),
                )
            };
        }

        unsafe {
            libc::close(client_fd);
        }

        *upstream_idx ^= 1;
    }
}

fn warmup_controls(controls: &mut [Option<ControlChannel>; 2]) {
    let results = thread::scope(|scope| {
        UPSTREAMS.map(|path| {
            scope
                .spawn(move || warmup_one(path))
                .join()
                .expect("warmup thread panicked")
        })
    });

    for (idx, stream) in results.into_iter().enumerate() {
        controls[idx] = stream;
    }
}

fn warmup_one(path: &str) -> Option<ControlChannel> {
    for _ in 0..WARMUP_ATTEMPTS {
        if let Some(stream) = connect_control(path) {
            return Some(stream);
        }
        thread::sleep(WARMUP_INTERVAL);
    }
    None
}

fn reconnect_controls(controls: &mut [Option<ControlChannel>; 2], reconnect_needed: &mut [bool; 2]) {
    for idx in 0..UPSTREAMS.len() {
        if !reconnect_needed[idx] || controls[idx].is_some() {
            continue;
        }

        if let Some(stream) = connect_control(UPSTREAMS[idx]) {
            controls[idx] = Some(stream);
            reconnect_needed[idx] = false;
        }
    }
}

fn handoff(
    client_fd: RawFd,
    first_idx: usize,
    controls: &mut [Option<ControlChannel>; 2],
    reconnect_needed: &mut [bool; 2],
) -> bool {
    if try_send(client_fd, first_idx, controls, reconnect_needed) {
        return true;
    }

    let alt = first_idx ^ 1;
    try_send(client_fd, alt, controls, reconnect_needed)
}

fn try_send(
    client_fd: RawFd,
    idx: usize,
    controls: &mut [Option<ControlChannel>; 2],
    reconnect_needed: &mut [bool; 2],
) -> bool {
    let Some(control) = controls[idx].as_mut() else {
        reconnect_needed[idx] = true;
        return false;
    };

    const HANDOFF_RETRY: Duration = Duration::from_micros(200);

    let start = std::time::Instant::now();
    loop {
        match control.send_fd(client_fd) {
            Ok(()) => return true,
            Err(err) if is_would_block(&err) => {
                if start.elapsed() >= HANDOFF_RETRY {
                    return false;
                }
                thread::yield_now();
            }
            Err(_) => {
                controls[idx] = None;
                reconnect_needed[idx] = true;
                return false;
            }
        }
    }
}

fn is_would_block(err: &io::Error) -> bool {
    err.raw_os_error() == Some(libc::EAGAIN)
}

fn make_listener(port: u16) -> io::Result<std::net::TcpListener> {
    let fd = unsafe { libc::socket(libc::AF_INET, libc::SOCK_STREAM, 0) };
    if fd < 0 {
        return Err(io::Error::last_os_error());
    }

    let result = (|| {
        set_int_sockopt(fd, libc::SOL_SOCKET, libc::SO_REUSEADDR, 1)?;
        set_int_sockopt(fd, libc::SOL_SOCKET, libc::SO_REUSEPORT, 1)?;

        let addr = libc::sockaddr_in {
            sin_family: libc::AF_INET as u16,
            sin_port: port.to_be(),
            sin_addr: libc::in_addr { s_addr: 0 },
            sin_zero: [0; 8],
        };

        let bind_result = unsafe {
            libc::bind(
                fd,
                &addr as *const _ as *const libc::sockaddr,
                std::mem::size_of::<libc::sockaddr_in>() as libc::socklen_t,
            )
        };
        if bind_result != 0 {
            return Err(io::Error::last_os_error());
        }

        let listen_result = unsafe { libc::listen(fd, BACKLOG) };
        if listen_result != 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(())
    })();

    if let Err(e) = result {
        unsafe {
            libc::close(fd);
        }
        return Err(e);
    }

    Ok(unsafe { std::net::TcpListener::from_raw_fd(fd) })
}

fn set_nonblocking(fd: RawFd) -> io::Result<()> {
    let flags = unsafe { libc::fcntl(fd, libc::F_GETFL) };
    if flags < 0 {
        return Err(io::Error::last_os_error());
    }

    if unsafe { libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK) } < 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(())
}

fn set_int_sockopt(fd: RawFd, level: i32, optname: i32, value: i32) -> io::Result<()> {
    let opt: libc::c_int = value;
    let result = unsafe {
        libc::setsockopt(
            fd,
            level,
            optname,
            &opt as *const _ as *const libc::c_void,
            std::mem::size_of::<libc::c_int>() as libc::socklen_t,
        )
    };
    if result != 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn configure_tcp(fd: RawFd) {
    let _ = set_int_sockopt(fd, libc::IPPROTO_TCP, libc::TCP_NODELAY, 1);
    let _ = set_int_sockopt(fd, libc::IPPROTO_TCP, libc::TCP_QUICKACK, 1);
}

fn connect_control(path: &str) -> Option<ControlChannel> {
    let stream = UnixStream::connect(path).ok()?;
    let fd = stream.as_raw_fd();
    set_nonblocking(fd).ok()?;
    let _ = set_int_sockopt(fd, libc::SOL_SOCKET, libc::SO_SNDBUF, CONTROL_SNDBUF);

    let mut control = [0u8; 64];
    let cmsg_len;
    unsafe {
        let hdr = control.as_mut_ptr().cast::<libc::cmsghdr>();
        (*hdr).cmsg_len =
            (std::mem::size_of::<libc::cmsghdr>() + std::mem::size_of::<RawFd>()) as _;
        (*hdr).cmsg_level = libc::SOL_SOCKET;
        (*hdr).cmsg_type = libc::SCM_RIGHTS;
        cmsg_len = (*hdr).cmsg_len as usize;
    }

    Some(ControlChannel {
        stream,
        byte: 0,
        control,
        cmsg_len,
    })
}

fn ignore_sigpipe() {
    unsafe {
        libc::signal(libc::SIGPIPE, libc::SIG_IGN);
    }
}
