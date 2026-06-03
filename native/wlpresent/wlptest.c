// Standalone Phase-1 proof: own xdg_toplevel (via the shim), present a moving 256x224 BGRA pattern,
// print fps. If this hits a clean ~60 FOREGROUNDED, owning a top-level is the windowed-60 fix.
// Build: see build.sh. Run: ./wlptest [seconds]
#include "wl_present.h"
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <time.h>

static double now_ms(void) {
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1000.0 + t.tv_nsec / 1e6;
}

int main(int argc, char **argv) {
    int seconds = argc > 1 ? atoi(argv[1]) : 15;
    const int FW = 256, FH = 224;
    uint8_t *frame = malloc(FW * FH * 4);

    void *p = wlp_create(768, 720, "wlptest (own xdg_toplevel)");
    if (!p) { fprintf(stderr, "wlp_create failed\n"); return 1; }

    double t0 = now_ms(), tWin = t0, prev = t0;
    long frames = 0, winFrames = 0;
    double winSum = 0, winMin = 1e9, winMax = 0;
    int tick = 0;

    while (now_ms() - t0 < seconds * 1000.0) {
        if (wlp_poll(p)) { printf("close requested\n"); break; }
        // moving gradient so the compositor sees real changing content
        for (int y = 0; y < FH; y++)
            for (int x = 0; x < FW; x++) {
                uint8_t *px = &frame[(y * FW + x) * 4];
                px[0] = (uint8_t)(x + tick);     // B
                px[1] = (uint8_t)(y + tick);     // G
                px[2] = (uint8_t)(x + y - tick); // R
                px[3] = 255;
            }
        tick++;
        if (wlp_present(p, frame, FW, FH) != 0) break;

        double t = now_ms(), dt = t - prev; prev = t;
        frames++; winFrames++; winSum += dt;
        if (dt < winMin) winMin = dt; if (dt > winMax) winMax = dt;
        if (t - tWin >= 1000.0) {
            double mean = winSum / winFrames;
            printf("  t=%2ds  fps=%.1f  mean=%.2fms  min=%.2f  max=%.2f\n",
                   (int)((t - t0) / 1000.0), 1000.0 / mean, mean, winMin, winMax);
            fflush(stdout);
            tWin = t; winFrames = 0; winSum = 0; winMin = 1e9; winMax = 0;
        }
    }
    printf("=== %ld frames, overall %.2f fps ===\n", frames, frames / ((now_ms() - t0) / 1000.0));
    wlp_destroy(p);
    free(frame);
    return 0;
}
