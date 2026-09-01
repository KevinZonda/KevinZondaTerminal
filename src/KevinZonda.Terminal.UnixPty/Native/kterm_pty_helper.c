#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#elif defined(__linux__)
#include <pty.h>
#else
#error "kterm-pty-helper supports only macOS and Linux"
#endif

enum
{
    FRAME_INPUT = 1,
    FRAME_RESIZE = 2,
    FRAME_CLOSE = 3,
    FRAME_OUTPUT = 1,
    FRAME_EXIT = 2,
    FRAME_ERROR = 3,
    FRAME_READY = 4,
    MAX_FRAME_BYTES = 16 * 1024 * 1024
};

static volatile sig_atomic_t received_signal = 0;

static void handle_signal(int signal_number)
{
    received_signal = signal_number;
}

static uint32_t read_u32_le(const unsigned char *buffer)
{
    return (uint32_t)buffer[0]
        | ((uint32_t)buffer[1] << 8)
        | ((uint32_t)buffer[2] << 16)
        | ((uint32_t)buffer[3] << 24);
}

static void write_u32_le(unsigned char *buffer, uint32_t value)
{
    buffer[0] = (unsigned char)(value & 0xffu);
    buffer[1] = (unsigned char)((value >> 8) & 0xffu);
    buffer[2] = (unsigned char)((value >> 16) & 0xffu);
    buffer[3] = (unsigned char)((value >> 24) & 0xffu);
}

static int write_all(int fd, const void *data, size_t length)
{
    const unsigned char *cursor = (const unsigned char *)data;
    while (length > 0)
    {
        ssize_t written = write(fd, cursor, length);
        if (written > 0)
        {
            cursor += written;
            length -= (size_t)written;
            continue;
        }
        if (written < 0 && errno == EINTR)
        {
            continue;
        }
        return -1;
    }
    return 0;
}

static int send_frame(unsigned char type, const void *payload, uint32_t length)
{
    unsigned char header[5];
    header[0] = type;
    write_u32_le(header + 1, length);
    if (write_all(STDOUT_FILENO, header, sizeof(header)) < 0)
    {
        return -1;
    }
    return length == 0 || write_all(STDOUT_FILENO, payload, length) == 0 ? 0 : -1;
}

static int send_error(const char *operation)
{
    char message[512];
    int count = snprintf(message, sizeof(message), "%s: %s", operation, strerror(errno));
    if (count < 0)
    {
        return -1;
    }
    size_t length = (size_t)count;
    if (length >= sizeof(message))
    {
        length = sizeof(message) - 1;
    }
    return send_frame(FRAME_ERROR, message, (uint32_t)length);
}

static int read_exact(int fd, void *data, size_t length)
{
    unsigned char *cursor = (unsigned char *)data;
    while (length > 0)
    {
        ssize_t count = read(fd, cursor, length);
        if (count > 0)
        {
            cursor += count;
            length -= (size_t)count;
            continue;
        }
        if (count < 0 && errno == EINTR)
        {
            continue;
        }
        return count == 0 ? 0 : -1;
    }
    return 1;
}

static int write_master(int master_fd, const unsigned char *data, size_t length)
{
    while (length > 0)
    {
        ssize_t count = write(master_fd, data, length);
        if (count > 0)
        {
            data += count;
            length -= (size_t)count;
            continue;
        }
        if (count < 0 && errno == EINTR)
        {
            continue;
        }
        if (count < 0 && (errno == EAGAIN || errno == EWOULDBLOCK))
        {
            struct pollfd descriptor = { master_fd, POLLOUT, 0 };
            if (poll(&descriptor, 1, 1000) >= 0)
            {
                continue;
            }
        }
        return -1;
    }
    return 0;
}

static int handle_command(int master_fd, pid_t child_pid, int *closing)
{
    unsigned char header[5];
    int header_result = read_exact(STDIN_FILENO, header, sizeof(header));
    if (header_result <= 0)
    {
        *closing = 1;
        (void)kill(-child_pid, SIGHUP);
        return header_result;
    }

    uint32_t length = read_u32_le(header + 1);
    if (length > MAX_FRAME_BYTES)
    {
        errno = EOVERFLOW;
        return -1;
    }

    unsigned char stack_payload[8];
    unsigned char *payload = stack_payload;
    if (length > sizeof(stack_payload))
    {
        payload = (unsigned char *)malloc(length);
        if (payload == NULL)
        {
            return -1;
        }
    }

    int result = 1;
    if (length > 0 && read_exact(STDIN_FILENO, payload, length) <= 0)
    {
        result = -1;
        goto cleanup;
    }

    switch (header[0])
    {
        case FRAME_INPUT:
            result = write_master(master_fd, payload, length) == 0 ? 1 : -1;
            break;

        case FRAME_RESIZE:
            if (length != 4)
            {
                errno = EINVAL;
                result = -1;
                break;
            }
            {
                struct winsize size;
                memset(&size, 0, sizeof(size));
                size.ws_col = (unsigned short)(payload[0] | ((unsigned short)payload[1] << 8));
                size.ws_row = (unsigned short)(payload[2] | ((unsigned short)payload[3] << 8));
                result = ioctl(master_fd, TIOCSWINSZ, &size) == 0 ? 1 : -1;
            }
            break;

        case FRAME_CLOSE:
            if (length != 0)
            {
                errno = EINVAL;
                result = -1;
                break;
            }
            *closing = 1;
            (void)kill(-child_pid, SIGHUP);
            result = 1;
            break;

        default:
            errno = EPROTO;
            result = -1;
            break;
    }

cleanup:
    if (payload != stack_payload)
    {
        free(payload);
    }
    return result;
}

static long long monotonic_milliseconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0)
    {
        return 0;
    }
    return (long long)value.tv_sec * 1000LL + value.tv_nsec / 1000000LL;
}

static void send_exit_status(int status)
{
    unsigned char payload[8];
    int signal_number = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
    int exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + signal_number;
    write_u32_le(payload, (uint32_t)exit_code);
    write_u32_le(payload + 4, (uint32_t)signal_number);
    (void)send_frame(FRAME_EXIT, payload, sizeof(payload));
}

static int parse_positive_size(const char *value, unsigned short *result)
{
    char *end = NULL;
    errno = 0;
    long parsed = strtol(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' || parsed <= 0 || parsed > 65535)
    {
        return -1;
    }
    *result = (unsigned short)parsed;
    return 0;
}

int main(int argc, char **argv)
{
    unsigned short columns = 80;
    unsigned short rows = 24;
    int target_index = -1;
    for (int index = 1; index < argc; index++)
    {
        if (strcmp(argv[index], "--cols") == 0 && index + 1 < argc)
        {
            if (parse_positive_size(argv[++index], &columns) < 0 || columns < 2)
            {
                fprintf(stderr, "invalid --cols value\n");
                return 2;
            }
        }
        else if (strcmp(argv[index], "--rows") == 0 && index + 1 < argc)
        {
            if (parse_positive_size(argv[++index], &rows) < 0)
            {
                fprintf(stderr, "invalid --rows value\n");
                return 2;
            }
        }
        else if (strcmp(argv[index], "--") == 0 && index + 1 < argc)
        {
            target_index = index + 1;
            break;
        }
        else
        {
            fprintf(stderr, "invalid helper arguments\n");
            return 2;
        }
    }
    if (target_index < 0)
    {
        fprintf(stderr, "missing PTY executable\n");
        return 2;
    }

    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = handle_signal;
    sigemptyset(&action.sa_mask);
    (void)sigaction(SIGTERM, &action, NULL);
    (void)sigaction(SIGINT, &action, NULL);
    (void)sigaction(SIGHUP, &action, NULL);
    signal(SIGPIPE, SIG_IGN);

    struct winsize size;
    memset(&size, 0, sizeof(size));
    size.ws_col = columns;
    size.ws_row = rows;

    int master_fd = -1;
    pid_t child_pid = forkpty(&master_fd, NULL, NULL, &size);
    if (child_pid < 0)
    {
        (void)send_error("forkpty");
        return 1;
    }
    if (child_pid == 0)
    {
        execvp(argv[target_index], &argv[target_index]);
        dprintf(STDERR_FILENO, "execvp(%s) failed: %s\r\n", argv[target_index], strerror(errno));
        _exit(127);
    }

    int flags = fcntl(master_fd, F_GETFL, 0);
    if (flags >= 0)
    {
        (void)fcntl(master_fd, F_SETFL, flags | O_NONBLOCK);
    }
    unsigned char ready_payload[4];
    write_u32_le(ready_payload, (uint32_t)child_pid);
    if (send_frame(FRAME_READY, ready_payload, sizeof(ready_payload)) < 0)
    {
        (void)kill(-child_pid, SIGHUP);
        close(master_fd);
        return 1;
    }

    int child_status = 0;
    int child_exited = 0;
    int closing = 0;
    int close_stage = 0;
    long long close_started = 0;
    unsigned char output_buffer[16384];

    while (!child_exited)
    {
        if (received_signal != 0)
        {
            int signal_number = received_signal;
            received_signal = 0;
            (void)kill(-child_pid, signal_number);
            closing = 1;
        }
        if (closing && close_started == 0)
        {
            close_started = monotonic_milliseconds();
            close_stage = 1;
        }

        struct pollfd descriptors[2];
        descriptors[0].fd = STDIN_FILENO;
        descriptors[0].events = closing ? 0 : POLLIN;
        descriptors[0].revents = 0;
        descriptors[1].fd = master_fd;
        descriptors[1].events = POLLIN;
        descriptors[1].revents = 0;
        int poll_result = poll(descriptors, 2, 100);
        if (poll_result < 0 && errno != EINTR)
        {
            (void)send_error("poll");
            (void)kill(-child_pid, SIGKILL);
            closing = 1;
        }

        if (!closing && (descriptors[0].revents & POLLIN) != 0)
        {
            if (handle_command(master_fd, child_pid, &closing) < 0)
            {
                (void)send_error("PTY command");
                (void)kill(-child_pid, SIGHUP);
                closing = 1;
            }
        }
        if (!closing && (descriptors[0].revents & (POLLHUP | POLLERR | POLLNVAL)) != 0)
        {
            (void)kill(-child_pid, SIGHUP);
            closing = 1;
        }

        if ((descriptors[1].revents & (POLLIN | POLLHUP)) != 0)
        {
            for (;;)
            {
                ssize_t count = read(master_fd, output_buffer, sizeof(output_buffer));
                if (count > 0)
                {
                    if (send_frame(FRAME_OUTPUT, output_buffer, (uint32_t)count) < 0)
                    {
                        (void)kill(-child_pid, SIGHUP);
                        closing = 1;
                        break;
                    }
                    continue;
                }
                if (count < 0 && errno == EINTR)
                {
                    continue;
                }
                break;
            }
        }

        pid_t wait_result = waitpid(child_pid, &child_status, WNOHANG);
        if (wait_result == child_pid)
        {
            child_exited = 1;
        }
        else if (wait_result < 0 && errno != EINTR)
        {
            (void)send_error("waitpid");
            child_status = 1 << 8;
            child_exited = 1;
        }

        if (closing && !child_exited)
        {
            long long elapsed = monotonic_milliseconds() - close_started;
            if (close_stage == 1 && elapsed >= 500)
            {
                (void)kill(-child_pid, SIGTERM);
                close_stage = 2;
            }
            else if (close_stage == 2 && elapsed >= 1500)
            {
                (void)kill(-child_pid, SIGKILL);
                close_stage = 3;
            }
        }
    }

    for (;;)
    {
        ssize_t count = read(master_fd, output_buffer, sizeof(output_buffer));
        if (count > 0)
        {
            if (send_frame(FRAME_OUTPUT, output_buffer, (uint32_t)count) < 0)
            {
                break;
            }
            continue;
        }
        if (count < 0 && errno == EINTR)
        {
            continue;
        }
        break;
    }

    close(master_fd);
    send_exit_status(child_status);
    return 0;
}
