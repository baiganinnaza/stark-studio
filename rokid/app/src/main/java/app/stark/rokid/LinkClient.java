package app.stark.rokid;

import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

import app.stark.rokid.core.StarkFormatException;
import app.stark.rokid.core.StudioLink;

/**
 * Опрос Stark Studio по Wi‑Fi (spec/stark-link-v1.md): раз в 3 с GET /api/v1/current, при новой версии
 * скачать пакет, проверить SHA-256 и отдать в MainActivity. Studio сама ничего не шлёт.
 */
final class LinkClient {
    interface Listener {
        /** Короткий статус для нижней строки экрана. */
        void onLinkStatus(String status, boolean ok);

        /** Новый пакет уже проверен по SHA-256. Вызывается в UI-потоке. */
        void onLinkPackage(StudioLink.Remote remote, byte[] data);
    }

    interface LoadedSha {
        String get();
    }

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final Listener listener;
    private final LoadedSha loaded;
    private volatile StudioLink.Pairing pairing;
    private volatile boolean running;
    private Thread thread;
    private final Object wake = new Object();

    LinkClient(Listener listener, LoadedSha loaded) {
        this.listener = listener;
        this.loaded = loaded;
    }

    synchronized void start(StudioLink.Pairing p) {
        pairing = p;
        if (p == null) {
            stop();
            return;
        }
        if (running) {
            wakeUp();
            return;
        }
        running = true;
        thread = new Thread(this::loop, "stark-link");
        thread.setDaemon(true);
        thread.start();
    }

    synchronized void stop() {
        running = false;
        wakeUp();
        thread = null;
    }

    void wakeUp() {
        synchronized (wake) {
            wake.notifyAll();
        }
    }

    private void loop() {
        int failures = 0;
        String lastShaTried = null;
        while (running) {
            StudioLink.Pairing p = pairing;
            if (p == null) break;
            try {
                String json = new String(get(p, p.currentUrl(), 64 * 1024), StandardCharsets.UTF_8);
                StudioLink.Remote r = StudioLink.parseCurrent(json);
                failures = 0;
                if (r == null) {
                    status("Studio " + p.host() + ": ждём «На очки»", true);
                } else if (StudioLink.isNew(r, loaded.get()) && !r.sha256.equals(lastShaTried)) {
                    status("Загрузка «" + (r.name == null ? r.id : r.name) + "» v" + r.version + "…", true);
                    byte[] data = get(p, p.packageUrl(), r.size + 1024);
                    StudioLink.verify(r, data); // повреждён → StarkFormatException, повтор со следующим опросом
                    lastShaTried = r.sha256;
                    ui.post(() -> listener.onLinkPackage(r, data));
                } else {
                    status("Studio " + p.host() + ": v" + r.version, true);
                }
            } catch (HttpError e) {
                failures++;
                status(e.code == 403 ? "Studio не принимает QR-код: отсканируйте новый" : "Studio: ошибка " + e.code, false);
            } catch (StarkFormatException e) {
                failures++;
                status(e.getMessage(), false);
            } catch (IOException e) {
                failures++;
                status("Нет связи со Studio " + p.host() + " (Wi‑Fi очков включён?)", false);
            }
            synchronized (wake) {
                if (!running) break;
                try {
                    wake.wait(StudioLink.pollDelayMs(failures));
                } catch (InterruptedException e) {
                    break;
                }
            }
        }
    }

    private void status(String s, boolean ok) {
        ui.post(() -> {
            if (running) listener.onLinkStatus(s, ok);
        });
    }

    static final class HttpError extends IOException {
        final int code;

        HttpError(int code) {
            super("HTTP " + code);
            this.code = code;
        }
    }

    private static byte[] get(StudioLink.Pairing p, String url, long maxBytes) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        try {
            c.setConnectTimeout(4000);
            c.setReadTimeout(15000);
            c.setUseCaches(false);
            c.setRequestProperty(StudioLink.TOKEN_HEADER, p.token);
            c.setRequestProperty(StudioLink.DEVICE_HEADER, "Rokid " + Build.MODEL);
            int code = c.getResponseCode();
            if (code != 200) throw new HttpError(code);
            try (InputStream in = c.getInputStream()) {
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                byte[] buf = new byte[32 * 1024];
                long total = 0;
                int n;
                while ((n = in.read(buf)) > 0) {
                    total += n;
                    if (total > maxBytes) throw new IOException("ответ больше ожидаемого");
                    out.write(buf, 0, n);
                }
                return out.toByteArray();
            }
        } finally {
            c.disconnect();
        }
    }
}
