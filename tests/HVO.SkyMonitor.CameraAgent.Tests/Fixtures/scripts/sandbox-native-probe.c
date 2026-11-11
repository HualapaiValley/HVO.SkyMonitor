/* Freestanding, bounded qualification payload. No libc, heap, fork, namespace creation or live I/O. */
#if defined(__x86_64__)
static long call(long n, long a, long b, long c, long d, long e, long f) {
    register long r10 __asm__("r10") = d, r8 __asm__("r8") = e, r9 __asm__("r9") = f;
    long result;
    __asm__ volatile("syscall" : "=a"(result) : "a"(n), "D"(a), "S"(b), "d"(c), "r"(r10), "r"(r8), "r"(r9) : "rcx", "r11", "memory");
    return result;
}
#define EXIT 231
#define WRITE 1
#define MEMFD 319
#define UNSHARE 272
#define MOUNT 165
#define SOCKET 41
#define MMAP 9
#ifndef PRELOAD
__asm__(".global _start\n_start:\nmov %rsp,%rdi\ncall entry\nud2");
#endif
#elif defined(__aarch64__)
static long call(long n, long a, long b, long c, long d, long e, long f) {
    register long x8 __asm__("x8") = n, x0 __asm__("x0") = a, x1 __asm__("x1") = b,
        x2 __asm__("x2") = c, x3 __asm__("x3") = d, x4 __asm__("x4") = e, x5 __asm__("x5") = f;
    __asm__ volatile("svc 0" : "+r"(x0) : "r"(x8), "r"(x1), "r"(x2), "r"(x3), "r"(x4), "r"(x5) : "memory");
    return x0;
}
#define EXIT 94
#define WRITE 64
#define MEMFD 279
#define UNSHARE 97
#define MOUNT 40
#define SOCKET 198
#define MMAP 222
#ifndef PRELOAD
__asm__(".global _start\n_start:\nmov x0,sp\nbl entry\nbrk 0");
#endif
#else
#error Unsupported sandbox probe architecture
#endif
#ifdef PRELOAD
__attribute__((constructor)) static void preload(void) { call(EXIT, 42, 0, 0, 0, 0, 0); }
#else
void entry(long *stack) {
    char **argv = (char **)(stack + 1);
    int result = 0;
    const char name[] = "bounded-probe";
    char mode = stack[0] > 1 ? argv[1][0] : 'x';
    if (mode == 'm') {
        /* Multiple anonymous files must all be refused, before even one byte can escape ScratchBytes. */
        for (int i = 0; i < 4; i++) if (call(MEMFD, (long)name, 0, 0, 0, 0, 0) != -1) result = 11;
    } else if (mode == 'n') {
        if (call(UNSHARE, 0x10000000, 0, 0, 0, 0, 0) >= 0) result = 12;
        if (call(MOUNT, (long)"none", (long)"/work", (long)"cgroup2", 0, 0, 0) != -1) result = 13;
    } else if (mode == 's') {
        if (call(SOCKET, 1, 1, 0, 0, 0, 0) != -1) result = 14;
        if (call(SOCKET, 2, 1, 0, 0, 0, 0) != -1) result = 15;
    } else if (mode == 'e') {
        if (call(MMAP, 0, 4096, 7, 0x22, -1, 0) != -1) result = 16;
    } else result = 17;
    if (!result) call(WRITE, 1, (long)"ok", 2, 0, 0, 0);
    call(EXIT, result, 0, 0, 0, 0, 0);
}
#endif
