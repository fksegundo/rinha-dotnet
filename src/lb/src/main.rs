use std::io;
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::net::UnixStream;
use std::thread;
use std::time::Duration;

const PORT: u16 = 9999;
const UPSTREAMS: [&str; 2] = ["/sockets/api1.sock", "/sockets/api2.sock"];
const BACKLOG: i32 = 65_535;
const MAX_EVENTS: i32 = 256;
const TCP_DEFER_ACCEPT: i32 = 9;
const WARMUP_ATTEMPTS: u32 = 300;
const WARMUP_INTERVAL: Duration = Duration::from_millis(100);
const RESPONSE_NOT_READY: &[u8] = b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n";

fn main() {
    ignore_sigpipe();

    let listener = make_listener(PORT).expect("failed to bind listener");
    let listener_fd = listener.as_raw_fd();
    set_nonblocking(listener_fd).expect("failed to set listener non-blocking");

    let mut controls: [Option<UnixStream>; 2] = [None, None];
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
    let mut events = [libc::epoll_event { events: 0, u64: 0 }; MAX_EVENTS as usize];

    loop {
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

            accept_burst(listener_fd, &mut upstream_idx, &mut controls);
        }
    }
}

fn accept_burst(listener_fd: RawFd, upstream_idx: &mut usize, controls: &mut [Option<UnixStream>; 2]) {
    loop {
        let client_fd = unsafe {
            libc::accept4(
                listener_fd,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                libc::SOCK_CLOEXEC,
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

        if !handoff(client_fd, *upstream_idx, controls) {
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

fn warmup_controls(controls: &mut [Option<UnixStream>; 2]) {
    for (idx, path) in UPSTREAMS.iter().enumerate() {
        for _ in 0..WARMUP_ATTEMPTS {
            if let Some(stream) = connect_control(path) {
                controls[idx] = Some(stream);
                break;
            }
            thread::sleep(WARMUP_INTERVAL);
        }
    }
}

fn handoff(client_fd: RawFd, first_idx: usize, controls: &mut [Option<UnixStream>; 2]) -> bool {
    if try_send(client_fd, first_idx, controls) {
        return true;
    }

    controls[first_idx] = connect_control(UPSTREAMS[first_idx]);
    if try_send(client_fd, first_idx, controls) {
        return true;
    }

    let alt = first_idx ^ 1;
    if try_send(client_fd, alt, controls) {
        return true;
    }

    controls[alt] = connect_control(UPSTREAMS[alt]);
    try_send(client_fd, alt, controls)
}

fn try_send(client_fd: RawFd, idx: usize, controls: &mut [Option<UnixStream>; 2]) -> bool {
    let Some(control) = controls[idx].as_ref() else {
        return false;
    };

    match send_fd(control.as_raw_fd(), client_fd) {
        Ok(()) => true,
        Err(_) => {
            controls[idx] = None;
            false
        }
    }
}

fn make_listener(port: u16) -> io::Result<std::net::TcpListener> {
    let fd = unsafe { libc::socket(libc::AF_INET, libc::SOCK_STREAM, 0) };
    if fd < 0 {
        return Err(io::Error::last_os_error());
    }

    let result = (|| {
        set_int_sockopt(fd, libc::SOL_SOCKET, libc::SO_REUSEADDR, 1)?;
        set_int_sockopt(fd, libc::SOL_SOCKET, libc::SO_REUSEPORT, 1)?;
        set_int_sockopt(fd, libc::IPPROTO_TCP, TCP_DEFER_ACCEPT, 1)?;

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

fn connect_control(path: &str) -> Option<UnixStream> {
    UnixStream::connect(path).ok()
}

fn send_fd(socket_fd: RawFd, fd_to_send: RawFd) -> io::Result<()> {
    let mut byte = [0u8; 1];
    let mut iov = libc::iovec {
        iov_base: byte.as_mut_ptr().cast(),
        iov_len: 1,
    };

    let mut control = [0u8; 64];
    let hdr = control.as_mut_ptr().cast::<libc::cmsghdr>();

    unsafe {
        (*hdr).cmsg_len =
            (std::mem::size_of::<libc::cmsghdr>() + std::mem::size_of::<RawFd>()) as _;
        (*hdr).cmsg_level = libc::SOL_SOCKET;
        (*hdr).cmsg_type = libc::SCM_RIGHTS;

        let data = control
            .as_mut_ptr()
            .add(std::mem::size_of::<libc::cmsghdr>())
            .cast::<RawFd>();
        *data = fd_to_send;

        let msg = libc::msghdr {
            msg_name: std::ptr::null_mut(),
            msg_namelen: 0,
            msg_iov: &mut iov,
            msg_iovlen: 1,
            msg_control: control.as_mut_ptr().cast(),
            msg_controllen: (*hdr).cmsg_len as _,
            msg_flags: 0,
        };

        let sent = libc::sendmsg(socket_fd, &msg, libc::MSG_NOSIGNAL);
        if sent != 1 {
            return Err(io::Error::last_os_error());
        }
    }

    Ok(())
}

fn ignore_sigpipe() {
    unsafe {
        libc::signal(libc::SIGPIPE, libc::SIG_IGN);
    }
}
