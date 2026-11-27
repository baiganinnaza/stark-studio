package app.stark.rokid.core;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.UnsupportedEncodingException;
import java.net.URI;
import java.net.URISyntaxException;
import java.net.URLDecoder;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/**
 * Протокол «Studio → очки» (spec/stark-link-v1.md): разбор ссылки из QR, ответа /api/v1/current
 * и проверки скачанного пакета. Сеть делает LinkClient, здесь только решения, чтобы их можно было тестировать.
 * Повторяет Stark.Core.Link.StudioLink из Unity-плеера.
 */
public final class StudioLink {
    public static final int PROTOCOL = 1;
    public static final String TOKEN_HEADER = "X-Stark-Token";
    public static final String DEVICE_HEADER = "X-Stark-Device";
    public static final long MAX_PACKAGE_BYTES = 300L * 1024 * 1024;

    private StudioLink() {
    }

    public static final class Pairing {
        public final String baseUrl; // http://192.168.1.10:8041 без «/» в конце
        public final String token;

        public Pairing(String baseUrl, String token) {
            while (baseUrl.endsWith("/")) baseUrl = baseUrl.substring(0, baseUrl.length() - 1);
            this.baseUrl = baseUrl;
            this.token = token;
        }

        public String currentUrl() {
            return baseUrl + "/api/v1/current";
        }

        public String packageUrl() {
            return baseUrl + "/api/v1/current.stark";
        }

        public String host() {
            try {
                return new URI(baseUrl).getRawAuthority();
            } catch (URISyntaxException e) {
                return baseUrl;
            }
        }
    }

    public static final class Remote {
        public String id;
        public String name;
        public int version;
        public String sha256;
        public long size;
        public int steps;
        public String sentAt;
    }

    /**
     * Ссылка из QR / со страницы Studio: stark://link?u=&lt;base&gt;&amp;t=&lt;token&gt;
     * или сам адрес страницы http://host:port/s/&lt;token&gt; (так QR сканирует камера очков). null = не ссылка Stark.
     */
    public static Pairing parse(String link) {
        if (link == null) return null;
        link = link.trim();
        if (link.isEmpty()) return null;
        String baseUrl;
        String token;
        String lower = link.toLowerCase(Locale.ROOT);
        if (lower.startsWith("stark:")) {
            int q = link.indexOf('?');
            if (q < 0) return null;
            String head = link.substring(0, q);
            while (head.endsWith("/")) head = head.substring(0, head.length() - 1);
            if (!head.equalsIgnoreCase("stark://link")) return null;
            Map<String, String> args = parseQuery(link.substring(q + 1));
            baseUrl = args.get("u");
            token = args.get("t");
            if (baseUrl == null || token == null) return null;
        } else if (lower.startsWith("http://") || lower.startsWith("https://")) {
            URI uri;
            try {
                uri = new URI(link);
            } catch (URISyntaxException e) {
                return null;
            }
            String path = uri.getRawPath();
            if (path == null || !path.startsWith("/s/") || uri.getRawAuthority() == null) return null;
            baseUrl = uri.getScheme() + "://" + uri.getRawAuthority();
            String t = path.substring(3);
            while (t.endsWith("/")) t = t.substring(0, t.length() - 1);
            token = decode(t);
        } else {
            return null;
        }
        if (!validToken(token)) return null;
        URI b;
        try {
            b = new URI(baseUrl);
        } catch (URISyntaxException e) {
            return null;
        }
        String scheme = b.getScheme() == null ? "" : b.getScheme().toLowerCase(Locale.ROOT);
        if (!scheme.equals("http") && !scheme.equals("https")) return null;
        if (b.getRawAuthority() == null || b.getHost() == null) return null;
        String p = b.getRawPath();
        if (p != null && p.replace("/", "").length() > 0) return null;
        if (b.getRawQuery() != null || b.getRawFragment() != null) return null;
        return new Pairing(scheme + "://" + b.getRawAuthority(), token);
    }

    static boolean validToken(String t) {
        if (t == null || t.length() < 16 || t.length() > 128) return false;
        for (int i = 0; i < t.length(); i++) {
            char c = t.charAt(i);
            boolean ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    private static Map<String, String> parseQuery(String q) {
        Map<String, String> d = new HashMap<>();
        for (String part : q.split("&")) {
            if (part.isEmpty()) continue;
            int eq = part.indexOf('=');
            String k = eq < 0 ? part : part.substring(0, eq);
            String v = eq < 0 ? "" : part.substring(eq + 1);
            d.put(decode(k), decode(v));
        }
        return d;
    }

    private static String decode(String s) {
        try {
            return URLDecoder.decode(s, "UTF-8");
        } catch (UnsupportedEncodingException | IllegalArgumentException e) {
            return s;
        }
    }

    /** Ответ /api/v1/current. null = Studio ещё ничего не отправила. */
    public static Remote parseCurrent(String json) throws StarkFormatException {
        JSONObject root;
        try {
            root = new JSONObject(json);
        } catch (JSONException e) {
            throw new StarkFormatException("Ответ Studio не JSON-объект");
        }
        int protocol = root.optInt("protocol", PROTOCOL);
        if (protocol > PROTOCOL)
            throw new StarkFormatException("Studio использует протокол v" + protocol + ", очки понимают v" + PROTOCOL + ". Обновите Stark для Rokid.");
        Object pkgObj = root.opt("package");
        if (pkgObj == null || pkgObj == JSONObject.NULL) return null;
        if (!(pkgObj instanceof JSONObject)) throw new StarkFormatException("package должен быть объектом");
        JSONObject pkg = (JSONObject) pkgObj;
        Remote r = new Remote();
        r.id = str(pkg, "id");
        r.name = str(pkg, "name");
        r.version = pkg.optInt("version", 0);
        String sha = str(pkg, "sha256");
        r.sha256 = sha == null ? null : sha.toLowerCase(Locale.ROOT);
        r.size = pkg.optLong("size", 0);
        r.steps = pkg.optInt("steps", 0);
        r.sentAt = str(pkg, "sentAt");
        if (r.id == null || r.id.isEmpty() || r.sha256 == null || r.sha256.length() != 64)
            throw new StarkFormatException("В ответе Studio нет id или sha256 пакета");
        if (r.size <= 0 || r.size > MAX_PACKAGE_BYTES)
            throw new StarkFormatException("Недопустимый размер пакета: " + r.size + " байт");
        return r;
    }

    private static String str(JSONObject o, String k) {
        Object v = o.opt(k);
        return v instanceof String ? (String) v : null;
    }

    public static boolean isNew(Remote remote, String loadedSha256) {
        return remote != null && !remote.sha256.equalsIgnoreCase(loadedSha256 == null ? "" : loadedSha256);
    }

    public static String sha256Hex(byte[] data) {
        try {
            byte[] h = MessageDigest.getInstance("SHA-256").digest(data);
            StringBuilder sb = new StringBuilder(h.length * 2);
            for (byte b : h) sb.append(String.format(Locale.ROOT, "%02x", b & 0xFF));
            return sb.toString();
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    public static void verify(Remote remote, byte[] data) throws StarkFormatException {
        if (data == null || data.length == 0) throw new StarkFormatException("Пустой ответ Studio");
        if (!sha256Hex(data).equals(remote.sha256))
            throw new StarkFormatException("Пакет пришёл повреждённым (контрольная сумма не совпала), попробую ещё раз");
    }

    /** Имя файла для сохранения: только безопасные символы из id проекта. */
    public static String fileNameFor(Remote remote) {
        StringBuilder sb = new StringBuilder();
        String id = remote.id == null ? "" : remote.id;
        for (int i = 0; i < id.length(); i++) {
            char c = id.charAt(i);
            boolean ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            sb.append(ok ? c : '-');
        }
        String name = sb.toString().replaceAll("^-+|-+$", "");
        if (name.isEmpty()) name = "studio";
        if (name.length() > 60) name = name.substring(0, 60);
        return name + ".stark";
    }

    /** Пауза между опросами, мс: 3 с, при ошибках сети растёт до 30 с. */
    public static long pollDelayMs(int consecutiveFailures) {
        if (consecutiveFailures <= 0) return 3000;
        return (long) Math.min(30000.0, 3000.0 * Math.pow(2, Math.min(consecutiveFailures, 4)));
    }
}
