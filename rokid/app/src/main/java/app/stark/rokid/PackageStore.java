package app.stark.rokid;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Environment;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import app.stark.rokid.core.StudioLink;

/**
 * Где лежат пакеты .stark на очках и что открыть при запуске.
 * <ul>
 *   <li>files/packages/ — пакеты из Studio по Wi‑Fi;</li>
 *   <li>/sdcard/Android/data/app.stark.rokid/files/ — сюда кладут пакет кабелем: {@code adb push x.stark …};</li>
 *   <li>/sdcard/Download/ — если прошивка даёт читать;</li>
 *   <li>образец «Редукторный модуль» внутри APK.</li>
 * </ul>
 */
final class PackageStore {
    static final String SAMPLE_ASSET = "gearbox_module.stark";
    private static final String PREFS = "stark";

    static final class Source {
        final String key; // "asset:имя" или "file:/путь"
        final String label;
        final long modified;

        Source(String key, String label, long modified) {
            this.key = key;
            this.label = label;
            this.modified = modified;
        }

        File file() {
            return key.startsWith("file:") ? new File(key.substring(5)) : null;
        }
    }

    private final Context ctx;
    private final SharedPreferences prefs;

    PackageStore(Context ctx) {
        this.ctx = ctx.getApplicationContext();
        this.prefs = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    SharedPreferences prefs() {
        return prefs;
    }

    File studioDir() {
        File d = new File(ctx.getFilesDir(), "packages");
        //noinspection ResultOfMethodCallIgnored
        d.mkdirs();
        return d;
    }

    File cableDir() {
        File d = ctx.getExternalFilesDir(null);
        if (d != null) //noinspection ResultOfMethodCallIgnored
            d.mkdirs();
        return d;
    }

    /** Все доступные пакеты: новые сверху, образец последним. */
    List<Source> list() {
        List<Source> files = new ArrayList<>();
        addDir(files, studioDir(), "Studio");
        addDir(files, cableDir(), "кабель");
        try {
            addDir(files, Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "Download");
        } catch (RuntimeException ignored) {
            // нет доступа к общей памяти
        }
        Collections.sort(files, (a, b) -> Long.compare(b.modified, a.modified));
        files.add(new Source("asset:" + SAMPLE_ASSET, "Образец: Редукторный модуль", 0));
        return files;
    }

    private static void addDir(List<Source> out, File dir, String where) {
        if (dir == null) return;
        File[] fs = dir.listFiles((d, name) -> name.toLowerCase().endsWith(".stark"));
        if (fs == null) return;
        for (File f : fs)
            if (f.isFile() && f.canRead()) out.add(new Source("file:" + f.getAbsolutePath(), f.getName() + " (" + where + ")", f.lastModified()));
    }

    /** Самый новый пакет, положенный кабелем (для автозагрузки после adb push). */
    Source newestCable() {
        List<Source> l = new ArrayList<>();
        addDir(l, cableDir(), "кабель");
        Source best = null;
        for (Source s : l) if (best == null || s.modified > best.modified) best = s;
        return best;
    }

    /** Что открыть при запуске: последний открытый, иначе новый с кабеля, иначе образец. */
    Source initial() {
        String last = prefs.getString("source", null);
        if (last != null) {
            if (last.startsWith("asset:")) return new Source(last, "Образец", 0);
            File f = new File(last.substring(5));
            if (f.canRead()) return new Source(last, f.getName(), f.lastModified());
        }
        Source cable = newestCable();
        return cable != null ? cable : new Source("asset:" + SAMPLE_ASSET, "Образец", 0);
    }

    byte[] read(Source s) throws IOException {
        if (s.key.startsWith("asset:")) {
            try (InputStream in = ctx.getAssets().open(s.key.substring(6))) {
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                byte[] buf = new byte[32 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                return out.toByteArray();
            }
        }
        return Files.readAllBytes(s.file().toPath());
    }

    /** Сохранить пакет из Studio (атомарно: сначала .tmp). */
    Source saveFromStudio(StudioLink.Remote r, byte[] data) throws IOException {
        File dest = new File(studioDir(), StudioLink.fileNameFor(r));
        File tmp = new File(dest.getPath() + ".tmp");
        try (FileOutputStream out = new FileOutputStream(tmp)) {
            out.write(data);
            out.getFD().sync();
        }
        if (!tmp.renameTo(dest)) {
            //noinspection ResultOfMethodCallIgnored
            dest.delete();
            if (!tmp.renameTo(dest)) throw new IOException("не удалось сохранить " + dest.getName());
        }
        return new Source("file:" + dest.getAbsolutePath(), dest.getName(), dest.lastModified());
    }

    void remember(Source s) {
        prefs.edit().putString("source", s.key).apply();
    }

    int savedStep(String productId) {
        return prefs.getInt("step:" + productId, 0);
    }

    void saveStep(String productId, int step) {
        prefs.edit().putInt("step:" + productId, step).apply();
    }

    StudioLink.Pairing pairing() {
        String u = prefs.getString("link.u", null), t = prefs.getString("link.t", null);
        if (u == null || t == null) return null;
        return new StudioLink.Pairing(u, t);
    }

    void savePairing(StudioLink.Pairing p) {
        if (p == null) prefs.edit().remove("link.u").remove("link.t").apply();
        else prefs.edit().putString("link.u", p.baseUrl).putString("link.t", p.token).apply();
    }
}
