package app.stark.rokid.core;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Минимальный разбор model.glb из пакета .stark: только треугольники (POSITION + indices) и матрицы узлов.
 * Узел с именем partId = деталь; безымянные дочерние узлы относятся к ближайшей именованной детали выше.
 * Результат: для каждой детали плоский массив треугольников в координатах сцены (метры, Y вверх)
 * и нормали граней, чтобы рисовать без индексов (GLES 2.0 на очках).
 */
public final class GlbModel {

    public static final class PartMesh {
        public final String partId;
        /** x,y,z на вершину, 3 вершины на треугольник. */
        public float[] positions;
        /** Нормаль грани для каждой вершины. */
        public float[] normals;

        PartMesh(String partId) {
            this.partId = partId;
        }

        public int vertexCount() {
            return positions.length / 3;
        }
    }

    public final Map<String, PartMesh> parts = new LinkedHashMap<>();
    public final float[] min = {Float.MAX_VALUE, Float.MAX_VALUE, Float.MAX_VALUE};
    public final float[] max = {-Float.MAX_VALUE, -Float.MAX_VALUE, -Float.MAX_VALUE};
    public int triangles;

    private JSONObject json;
    private ByteBuffer bin;
    private Set<String> knownParts;
    private final Map<String, List<float[]>> chunks = new LinkedHashMap<>();

    /** knownParts: id деталей из steps.json (узлы с другими именами считаются частью родителя). */
    public static GlbModel parse(byte[] glb, Set<String> knownParts) throws StarkFormatException {
        GlbModel m = new GlbModel();
        m.knownParts = knownParts;
        try {
            m.read(glb);
        } catch (StarkFormatException e) {
            throw e;
        } catch (Exception e) { // JSONException на Android проверяемое, в org.json для JVM нет
            throw new StarkFormatException("model.glb не читается: " + e.getMessage(), e);
        }
        return m;
    }

    private void read(byte[] glb) throws StarkFormatException, JSONException {
        ByteBuffer b = ByteBuffer.wrap(glb).order(ByteOrder.LITTLE_ENDIAN);
        if (b.getInt(0) != 0x46546C67) throw new StarkFormatException("model.glb: нет сигнатуры glTF");
        int length = Math.min(b.getInt(8), glb.length);
        int off = 12;
        while (off + 8 <= length) {
            int chunkLen = b.getInt(off);
            int type = b.getInt(off + 4);
            int start = off + 8;
            if (chunkLen < 0 || start + chunkLen > length) throw new StarkFormatException("model.glb: обрезанный chunk");
            if (type == 0x4E4F534A) { // JSON
                json = new JSONObject(new String(glb, start, chunkLen, StandardCharsets.UTF_8));
            } else if (type == 0x004E4942) { // BIN
                ByteBuffer slice = ByteBuffer.wrap(glb, start, chunkLen).slice();
                bin = slice.order(ByteOrder.LITTLE_ENDIAN);
            }
            off = start + ((chunkLen + 3) & ~3);
        }
        if (json == null) throw new StarkFormatException("model.glb: нет JSON");

        JSONArray nodes = json.optJSONArray("nodes");
        if (nodes == null) return;
        JSONArray roots;
        JSONArray scenes = json.optJSONArray("scenes");
        if (scenes != null && scenes.length() > 0) {
            roots = scenes.getJSONObject(Math.max(0, json.optInt("scene", 0))).optJSONArray("nodes");
        } else {
            roots = new JSONArray();
            for (int i = 0; i < nodes.length(); i++) roots.put(i);
        }
        if (roots == null) return;
        for (int i = 0; i < roots.length(); i++) visit(nodes, roots.getInt(i), Mat4.identity(), null, 0);

        for (Map.Entry<String, List<float[]>> e : chunks.entrySet()) {
            int total = 0;
            for (float[] c : e.getValue()) total += c.length;
            PartMesh pm = new PartMesh(e.getKey());
            pm.positions = new float[total];
            int o = 0;
            for (float[] c : e.getValue()) {
                System.arraycopy(c, 0, pm.positions, o, c.length);
                o += c.length;
            }
            pm.normals = faceNormals(pm.positions);
            parts.put(pm.partId, pm);
            triangles += total / 9;
        }
        chunks.clear();
    }

    private void visit(JSONArray nodes, int index, float[] parent, String partId, int depth) throws JSONException, StarkFormatException {
        if (depth > 64) throw new StarkFormatException("model.glb: слишком глубокое дерево узлов");
        JSONObject n = nodes.getJSONObject(index);
        float[] world = Mat4.mul(parent, localMatrix(n));
        String name = n.optString("name", "");
        String id = partId;
        if (!name.isEmpty() && (knownParts == null || knownParts.contains(name))) id = name;
        if (n.has("mesh") && id != null) addMesh(json.getJSONArray("meshes").getJSONObject(n.getInt("mesh")), world, id);
        JSONArray children = n.optJSONArray("children");
        if (children != null) for (int i = 0; i < children.length(); i++) visit(nodes, children.getInt(i), world, id, depth + 1);
    }

    private static float[] localMatrix(JSONObject n) throws JSONException {
        JSONArray m = n.optJSONArray("matrix");
        if (m != null && m.length() == 16) {
            float[] r = new float[16];
            for (int i = 0; i < 16; i++) r[i] = (float) m.getDouble(i);
            return r;
        }
        float[] t = vec(n.optJSONArray("translation"), new float[]{0, 0, 0});
        float[] q = vec(n.optJSONArray("rotation"), new float[]{0, 0, 0, 1});
        float[] s = vec(n.optJSONArray("scale"), new float[]{1, 1, 1});
        return Mat4.trs(t, q, s);
    }

    private static float[] vec(JSONArray a, float[] def) throws JSONException {
        if (a == null || a.length() != def.length) return def;
        float[] r = new float[def.length];
        for (int i = 0; i < r.length; i++) r[i] = (float) a.getDouble(i);
        return r;
    }

    private void addMesh(JSONObject mesh, float[] world, String partId) throws JSONException, StarkFormatException {
        JSONArray prims = mesh.optJSONArray("primitives");
        if (prims == null) return;
        for (int p = 0; p < prims.length(); p++) {
            JSONObject prim = prims.getJSONObject(p);
            if (prim.optInt("mode", 4) != 4) continue; // только TRIANGLES
            JSONObject attrs = prim.getJSONObject("attributes");
            if (!attrs.has("POSITION")) continue;
            float[] pos = readVec3(attrs.getInt("POSITION"));
            int vcount = pos.length / 3;
            int[] idx = prim.has("indices") ? readIndices(prim.getInt("indices")) : null;
            int tri = (idx != null ? idx.length : vcount) / 3;
            float[] out = new float[tri * 9];
            for (int t = 0; t < tri * 3; t++) {
                int v = idx != null ? idx[t] : t;
                if (v < 0 || v >= vcount) throw new StarkFormatException("model.glb: индекс вне массива вершин");
                float x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
                float wx = world[0] * x + world[4] * y + world[8] * z + world[12];
                float wy = world[1] * x + world[5] * y + world[9] * z + world[13];
                float wz = world[2] * x + world[6] * y + world[10] * z + world[14];
                out[t * 3] = wx;
                out[t * 3 + 1] = wy;
                out[t * 3 + 2] = wz;
                if (wx < min[0]) min[0] = wx;
                if (wy < min[1]) min[1] = wy;
                if (wz < min[2]) min[2] = wz;
                if (wx > max[0]) max[0] = wx;
                if (wy > max[1]) max[1] = wy;
                if (wz > max[2]) max[2] = wz;
            }
            List<float[]> list = chunks.get(partId);
            if (list == null) chunks.put(partId, list = new ArrayList<>());
            list.add(out);
        }
    }

    private ByteBuffer view(JSONObject acc, int[] strideOut, int elementBytes) throws JSONException, StarkFormatException {
        if (bin == null) throw new StarkFormatException("model.glb: нет бинарного буфера");
        if (!acc.has("bufferView")) throw new StarkFormatException("model.glb: accessor без bufferView");
        JSONObject bv = json.getJSONArray("bufferViews").getJSONObject(acc.getInt("bufferView"));
        if (bv.optInt("buffer", 0) != 0) throw new StarkFormatException("model.glb: внешние буферы не поддерживаются");
        int start = bv.optInt("byteOffset", 0) + acc.optInt("byteOffset", 0);
        int stride = bv.optInt("byteStride", 0);
        strideOut[0] = stride == 0 ? elementBytes : stride;
        int count = acc.getInt("count");
        long need = (long) start + (long) (count - 1) * strideOut[0] + elementBytes;
        if (count > 0 && (start < 0 || need > bin.limit())) throw new StarkFormatException("model.glb: accessor за пределами буфера");
        return bin;
    }

    private float[] readVec3(int accessor) throws JSONException, StarkFormatException {
        JSONObject acc = json.getJSONArray("accessors").getJSONObject(accessor);
        if (acc.getInt("componentType") != 5126 || !"VEC3".equals(acc.getString("type")))
            throw new StarkFormatException("model.glb: POSITION должен быть float VEC3");
        int[] stride = new int[1];
        ByteBuffer b = view(acc, stride, 12);
        JSONObject bv = json.getJSONArray("bufferViews").getJSONObject(acc.getInt("bufferView"));
        int start = bv.optInt("byteOffset", 0) + acc.optInt("byteOffset", 0);
        int count = acc.getInt("count");
        float[] r = new float[count * 3];
        for (int i = 0; i < count; i++) {
            int o = start + i * stride[0];
            r[i * 3] = b.getFloat(o);
            r[i * 3 + 1] = b.getFloat(o + 4);
            r[i * 3 + 2] = b.getFloat(o + 8);
        }
        return r;
    }

    private int[] readIndices(int accessor) throws JSONException, StarkFormatException {
        JSONObject acc = json.getJSONArray("accessors").getJSONObject(accessor);
        int ct = acc.getInt("componentType");
        int size = ct == 5125 ? 4 : ct == 5123 ? 2 : ct == 5121 ? 1 : -1;
        if (size < 0) throw new StarkFormatException("model.glb: неизвестный тип индексов " + ct);
        int[] stride = new int[1];
        ByteBuffer b = view(acc, stride, size);
        JSONObject bv = json.getJSONArray("bufferViews").getJSONObject(acc.getInt("bufferView"));
        int start = bv.optInt("byteOffset", 0) + acc.optInt("byteOffset", 0);
        int count = acc.getInt("count");
        int[] r = new int[count];
        for (int i = 0; i < count; i++) {
            int o = start + i * stride[0];
            r[i] = size == 4 ? b.getInt(o) : size == 2 ? (b.getShort(o) & 0xFFFF) : (b.get(o) & 0xFF);
        }
        return r;
    }

    static float[] faceNormals(float[] p) {
        float[] n = new float[p.length];
        for (int t = 0; t + 8 < p.length; t += 9) {
            float ax = p[t + 3] - p[t], ay = p[t + 4] - p[t + 1], az = p[t + 5] - p[t + 2];
            float bx = p[t + 6] - p[t], by = p[t + 7] - p[t + 1], bz = p[t + 8] - p[t + 2];
            float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            float len = (float) Math.sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12f) {
                nx /= len;
                ny /= len;
                nz /= len;
            } else {
                nx = 0;
                ny = 1;
                nz = 0;
            }
            for (int v = 0; v < 3; v++) {
                n[t + v * 3] = nx;
                n[t + v * 3 + 1] = ny;
                n[t + v * 3 + 2] = nz;
            }
        }
        return n;
    }

    public float[] center() {
        return new float[]{(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2};
    }

    public float radius() {
        float dx = max[0] - min[0], dy = max[1] - min[1], dz = max[2] - min[2];
        float r = (float) Math.sqrt(dx * dx + dy * dy + dz * dz) / 2;
        return r > 0 ? r : 0.1f;
    }
}
