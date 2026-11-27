package app.stark.rokid.core;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/**
 * Пакет .stark (spec/stark-format-v1.md) без Android: разбор zip, manifest.json, steps.json, hints/*.json.
 * Те же проверки, что в ядре Unity-плеера (StarkPackageLoader.cs), чтобы оба плеера принимали одни и те же пакеты.
 */
public final class StarkPackage {
    public static final int SUPPORTED_FORMAT_VERSION = 1;
    public static final long MAX_ENTRY_BYTES = 300L * 1024 * 1024;

    public static final class Part {
        public String id;
        public String name;
        public int qty = 1;
        public List<String> tags = new ArrayList<>();

        public boolean isFastener() {
            return tags.contains("fastener");
        }
    }

    public static final class Fastener {
        public List<String> partIds = new ArrayList<>();
        public String name;
        public int qty;
        public Double torqueNm;
        public Map<String, float[][]> partPaths = new HashMap<>();
    }

    public static final class Step {
        public int index;
        public String id;
        public String title;
        public List<String> parts = new ArrayList<>();
        public List<Fastener> fasteners = new ArrayList<>();
        public float[][] insertPath;
        public Map<String, float[][]> partPaths = new HashMap<>();
        public String tool;
        public String hintKey;
        public List<String> checks = new ArrayList<>();
        public String review;

        /** Все детали шага: основные + каждый экземпляр крепежа. */
        public List<String> allPartIds() {
            List<String> all = new ArrayList<>(parts);
            for (Fastener f : fasteners) all.addAll(f.partIds);
            return all;
        }

        /** Путь установки детали (смещения в метрах), null = деталь стоит на месте. */
        public float[][] pathFor(String partId) {
            float[][] p = partPaths.get(partId);
            if (p != null) return p;
            for (Fastener f : fasteners) {
                p = f.partPaths.get(partId);
                if (p != null) return p;
            }
            if (!parts.isEmpty() && parts.get(0).equals(partId)) return insertPath;
            return null;
        }
    }

    public int formatVersion;
    public String productId;
    public String productName;
    public String createdAt;
    public String generator;
    public List<String> languages = new ArrayList<>();
    public final List<Part> parts = new ArrayList<>();
    public final List<Step> steps = new ArrayList<>();
    public final Map<String, Map<String, String>> hints = new HashMap<>();
    public byte[] modelGlb;

    private final Map<String, Part> partIndex = new HashMap<>();
    private final Map<String, Integer> stepOfPart = new HashMap<>();

    public Part findPart(String id) {
        return partIndex.get(id);
    }

    /** Номер шага, в котором ставится деталь, или -1. */
    public int stepOf(String partId) {
        Integer i = stepOfPart.get(partId);
        return i == null ? -1 : i;
    }

    public String partName(String id) {
        Part p = partIndex.get(id);
        return p == null || p.name == null ? id : p.name;
    }

    /** Подсказка шага на языке lang; если её нет, то ru, потом любая. null, если подсказок нет. */
    public String hint(Step step, String lang) {
        if (step.hintKey == null) return null;
        String[] order = {lang, "ru", "en", "kk"};
        for (String l : order) {
            Map<String, String> t = hints.get(l);
            if (t != null && t.get(step.hintKey) != null && !t.get(step.hintKey).isEmpty()) return t.get(step.hintKey);
        }
        for (Map<String, String> t : hints.values()) {
            String s = t.get(step.hintKey);
            if (s != null && !s.isEmpty()) return s;
        }
        return null;
    }

    public static StarkPackage load(byte[] archive) throws StarkFormatException {
        if (archive == null || archive.length == 0) throw new StarkFormatException("Пустой файл пакета");
        Map<String, byte[]> entries = readZip(archive);

        StarkPackage pkg = new StarkPackage();
        try {
            JSONObject manifest = new JSONObject(text(entries, "manifest.json"));
            pkg.formatVersion = manifest.optInt("formatVersion", -1);
            if (pkg.formatVersion != SUPPORTED_FORMAT_VERSION)
                throw new StarkFormatException("Версия формата " + pkg.formatVersion
                        + " не поддерживается (плеер понимает v" + SUPPORTED_FORMAT_VERSION + "). Обновите Stark для Rokid.");
            pkg.productId = optStr(manifest, "productId");
            pkg.productName = optStr(manifest, "productName");
            pkg.createdAt = optStr(manifest, "createdAt");
            pkg.generator = optStr(manifest, "generator");
            pkg.languages = strings(manifest.optJSONArray("languages"));
            String units = manifest.optString("units", "m");
            String up = manifest.optString("upAxis", "Y");
            if (!"m".equals(units)) throw new StarkFormatException("Единицы модели '" + units + "', ожидались метры");
            if (!"Y".equals(up)) throw new StarkFormatException("Ось вверх '" + up + "', ожидалась Y");

            byte[] glb = entries.get("model.glb");
            if (glb == null || glb.length < 12) throw new StarkFormatException("В пакете нет model.glb");
            if (glb[0] != 0x67 || glb[1] != 0x6C || glb[2] != 0x54 || glb[3] != 0x46)
                throw new StarkFormatException("model.glb повреждён (нет сигнатуры glTF)");
            pkg.modelGlb = glb;

            JSONObject root = new JSONObject(text(entries, "steps.json"));
            JSONArray parts = root.optJSONArray("parts");
            if (parts != null) for (int i = 0; i < parts.length(); i++) {
                JSONObject o = parts.getJSONObject(i);
                Part p = new Part();
                p.id = optStr(o, "id");
                p.name = optStr(o, "name");
                p.qty = o.optInt("qty", 1);
                p.tags = strings(o.optJSONArray("tags"));
                if (p.id == null || p.id.isEmpty()) throw new StarkFormatException("Деталь без id");
                pkg.parts.add(p);
            }
            JSONArray steps = root.optJSONArray("steps");
            if (steps != null) for (int i = 0; i < steps.length(); i++) {
                JSONObject o = steps.getJSONObject(i);
                Step s = new Step();
                s.index = i;
                s.id = optStr(o, "id");
                s.title = optStr(o, "title");
                s.parts = strings(o.optJSONArray("parts"));
                s.insertPath = path(o.opt("insertPath"));
                s.partPaths = pathMap(o.opt("partPaths"));
                s.tool = optStr(o, "tool");
                s.hintKey = optStr(o, "hintKey");
                s.checks = strings(o.optJSONArray("checks"));
                s.review = reviewText(o.opt("review"));
                JSONArray fs = o.optJSONArray("fasteners");
                if (fs != null) for (int k = 0; k < fs.length(); k++) {
                    JSONObject fo = fs.getJSONObject(k);
                    Fastener f = new Fastener();
                    f.partIds = strings(fo.optJSONArray("partIds"));
                    if (f.partIds.isEmpty() && fo.opt("partId") instanceof String) f.partIds.add(fo.getString("partId"));
                    f.name = optStr(fo, "name");
                    f.qty = fo.optInt("qty", f.partIds.size());
                    Object t = fo.opt("torqueNm");
                    f.torqueNm = t instanceof Number ? ((Number) t).doubleValue() : null;
                    f.partPaths = pathMap(fo.opt("partPaths"));
                    s.fasteners.add(f);
                }
                pkg.steps.add(s);
            }
            for (Map.Entry<String, byte[]> e : entries.entrySet()) {
                String name = e.getKey();
                if (name.startsWith("hints/") && name.endsWith(".json")) {
                    String lang = name.substring(6, name.length() - 5);
                    JSONObject h = new JSONObject(new String(e.getValue(), StandardCharsets.UTF_8));
                    Map<String, String> table = new HashMap<>();
                    Iterator<String> keys = h.keys();
                    while (keys.hasNext()) {
                        String k = keys.next();
                        Object v = h.opt(k);
                        if (v instanceof String) table.put(k, (String) v);
                    }
                    pkg.hints.put(lang, table);
                }
            }
        } catch (JSONException e) {
            throw new StarkFormatException("Ошибка JSON в пакете: " + e.getMessage(), e);
        }
        pkg.index();
        pkg.validate();
        return pkg;
    }

    private void index() {
        for (Part p : parts) partIndex.put(p.id, p);
        for (Step s : steps) for (String id : s.allPartIds()) stepOfPart.put(id, s.index);
    }

    private void validate() throws StarkFormatException {
        if (steps.isEmpty()) throw new StarkFormatException("В пакете нет ни одного шага");
        Set<String> seen = new HashSet<>();
        for (Step s : steps) {
            List<String> ids = s.allPartIds();
            if (ids.isEmpty()) throw new StarkFormatException("Шаг " + s.id + " без деталей");
            for (String id : ids) {
                if (findPart(id) == null) throw new StarkFormatException("Шаг " + s.id + ": неизвестная деталь " + id);
                if (!seen.add(id)) throw new StarkFormatException("Деталь " + id + " встречается в нескольких шагах");
            }
        }
    }

    private static Map<String, byte[]> readZip(byte[] bytes) throws StarkFormatException {
        Map<String, byte[]> result = new LinkedHashMap<>();
        try (ZipInputStream zip = new ZipInputStream(new ByteArrayInputStream(bytes))) {
            ZipEntry e;
            byte[] buf = new byte[16 * 1024];
            while ((e = zip.getNextEntry()) != null) {
                if (e.isDirectory()) continue;
                String name = e.getName().replace('\\', '/');
                while (name.startsWith("/")) name = name.substring(1);
                if (name.contains("..")) continue; // защита от путей вне архива
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                int n;
                long total = 0;
                while ((n = zip.read(buf)) > 0) {
                    total += n;
                    if (total > MAX_ENTRY_BYTES) throw new StarkFormatException("Слишком большой файл в пакете: " + name);
                    out.write(buf, 0, n);
                }
                result.put(name, out.toByteArray());
            }
        } catch (IOException ex) {
            throw new StarkFormatException("Файл не является пакетом .stark (не zip)", ex);
        }
        if (result.isEmpty()) throw new StarkFormatException("Файл не является пакетом .stark (не zip)");
        return result;
    }

    private static String text(Map<String, byte[]> entries, String name) throws StarkFormatException {
        byte[] b = entries.get(name);
        if (b == null) throw new StarkFormatException("В пакете нет " + name);
        return new String(b, StandardCharsets.UTF_8);
    }

    private static String optStr(JSONObject o, String k) {
        Object v = o.opt(k);
        return v instanceof String ? (String) v : null;
    }

    private static List<String> strings(JSONArray a) {
        if (a == null) return new ArrayList<>();
        List<String> list = new ArrayList<>(a.length());
        for (int i = 0; i < a.length(); i++) {
            Object v = a.opt(i);
            if (v instanceof String) list.add((String) v);
        }
        return list;
    }

    private static String reviewText(Object review) {
        if (review == null || review == JSONObject.NULL) return null;
        if (review instanceof String) return (String) review;
        if (review instanceof JSONObject) {
            JSONObject d = (JSONObject) review;
            String r = optStr(d, "reason");
            if (r == null) r = optStr(d, "message");
            return r == null ? "Требует проверки" : r;
        }
        return "Требует проверки";
    }

    private static Map<String, float[][]> pathMap(Object o) {
        Map<String, float[][]> map = new HashMap<>();
        if (o instanceof JSONObject) {
            JSONObject d = (JSONObject) o;
            Iterator<String> keys = d.keys();
            while (keys.hasNext()) {
                String k = keys.next();
                float[][] p = path(d.opt(k));
                if (p != null) map.put(k, p);
            }
        }
        return map;
    }

    private static float[][] path(Object o) {
        if (!(o instanceof JSONArray)) return null;
        JSONArray pts = (JSONArray) o;
        List<float[]> list = new ArrayList<>();
        for (int i = 0; i < pts.length(); i++) {
            JSONArray p = pts.optJSONArray(i);
            if (p == null || p.length() < 3) continue;
            list.add(new float[]{(float) p.optDouble(0, 0), (float) p.optDouble(1, 0), (float) p.optDouble(2, 0)});
        }
        return list.isEmpty() ? null : list.toArray(new float[0][]);
    }

    public List<Step> stepsView() {
        return Collections.unmodifiableList(steps);
    }
}
