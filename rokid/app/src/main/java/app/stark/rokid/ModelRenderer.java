package app.stark.rokid;

import android.opengl.GLES20;
import android.opengl.GLSurfaceView;
import android.opengl.Matrix;
import android.os.SystemClock;
import android.util.Log;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.ArrayList;
import java.util.List;

import javax.microedition.khronos.egl.EGLConfig;
import javax.microedition.khronos.opengles.GL10;

import app.stark.rokid.core.GlbModel;
import app.stark.rokid.core.StarkPackage;
import app.stark.rokid.core.StepHud;

/**
 * 3D-схема изделия на монохромном экране очков, перед глазами (у Rokid AI Glasses нет трекинга стола).
 * Готовые детали тусклые, детали текущего шага яркие и «прилетают» по своему пути установки,
 * будущие детали не рисуются. Детали текущего шага видны и сквозь корпус (просвет).
 * Чёрный цвет на волноводе прозрачен, поэтому фон чёрный.
 */
final class ModelRenderer implements GLSurfaceView.Renderer {
    private static final String TAG = "StarkRenderer";

    private static final String VS =
            "uniform mat4 uMVP;\n" +
            "uniform vec3 uOffset;\n" +
            "uniform vec3 uLight;\n" +
            "attribute vec3 aPos;\n" +
            "attribute vec3 aNrm;\n" +
            "varying float vShade;\n" +
            "void main() {\n" +
            "  gl_Position = uMVP * vec4(aPos + uOffset, 1.0);\n" +
            "  float d = abs(dot(normalize(aNrm), uLight));\n" +
            "  vShade = 0.30 + 0.70 * d;\n" +
            "}\n";
    private static final String FS =
            "precision mediump float;\n" +
            "uniform vec3 uColor;\n" +
            "varying float vShade;\n" +
            "void main() { gl_FragColor = vec4(uColor * vShade, 1.0); }\n";

    private static final float[] GREEN = {0.25f, 1.0f, 0.42f};

    private static final class GpuPart {
        String partId;
        int vbo;
        int count;
        FloatBuffer data; // хранится до загрузки и для пересоздания контекста
        float[] center;
    }

    // Меняется из UI-потока, читается в GL-потоке
    private volatile Scene pending;
    private volatile int step;
    private volatile long animStart = SystemClock.uptimeMillis();
    private volatile boolean autoRotate = true;
    private volatile int vpX, vpY, vpW, vpH; // область 3D в пикселях GL (снизу вверх)

    private Scene scene;
    private final List<GpuPart> gpu = new ArrayList<>();
    private int program, uMVP, uOffset, uLight, uColor, aPos, aNrm;
    private int surfaceW, surfaceH;
    private final float[] proj = new float[16], view = new float[16], mvp = new float[16];
    private final float[] camTarget = new float[3];
    private boolean camInit;

    static final class Scene {
        final StarkPackage pkg;
        final GlbModel model;

        Scene(StarkPackage pkg, GlbModel model) {
            this.pkg = pkg;
            this.model = model;
        }
    }

    void setScene(StarkPackage pkg, GlbModel model) {
        pending = new Scene(pkg, model);
    }

    void setStep(int step) {
        if (this.step != step) {
            this.step = step;
            replay();
        }
    }

    void replay() {
        animStart = SystemClock.uptimeMillis();
    }

    void setAutoRotate(boolean on) {
        autoRotate = on;
    }

    /** Прямоугольник 3D-области в координатах экрана (y сверху). */
    void setViewport(int left, int top, int width, int height, int screenHeight) {
        vpX = left;
        vpY = screenHeight - (top + height);
        vpW = width;
        vpH = height;
    }

    @Override
    public void onSurfaceCreated(GL10 unused, EGLConfig config) {
        program = link(compile(GLES20.GL_VERTEX_SHADER, VS), compile(GLES20.GL_FRAGMENT_SHADER, FS));
        uMVP = GLES20.glGetUniformLocation(program, "uMVP");
        uOffset = GLES20.glGetUniformLocation(program, "uOffset");
        uLight = GLES20.glGetUniformLocation(program, "uLight");
        uColor = GLES20.glGetUniformLocation(program, "uColor");
        aPos = GLES20.glGetAttribLocation(program, "aPos");
        aNrm = GLES20.glGetAttribLocation(program, "aNrm");
        // Контекст пересоздан: старые VBO недействительны, загружаем заново из сохранённых буферов
        for (GpuPart g : gpu) g.vbo = upload(g.data);
        GLES20.glClearColor(0, 0, 0, 1);
    }

    @Override
    public void onSurfaceChanged(GL10 unused, int width, int height) {
        surfaceW = width;
        surfaceH = height;
        if (vpW == 0) {
            vpX = 0;
            vpY = 0;
            vpW = width;
            vpH = height;
        }
    }

    @Override
    public void onDrawFrame(GL10 unused) {
        Scene p = pending;
        if (p != null) {
            pending = null;
            swapScene(p);
        }
        GLES20.glViewport(0, 0, surfaceW, surfaceH);
        GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT | GLES20.GL_DEPTH_BUFFER_BIT);
        if (scene == null || vpW <= 0 || vpH <= 0) return;

        GLES20.glViewport(vpX, vpY, vpW, vpH);
        GLES20.glEnable(GLES20.GL_DEPTH_TEST);
        GLES20.glDisable(GLES20.GL_CULL_FACE);
        GLES20.glUseProgram(program);

        int current = step;
        float t = (SystemClock.uptimeMillis() - animStart) / 1000f;
        updateCamera(current, SystemClock.uptimeMillis() / 1000f);
        GLES20.glUniformMatrix4fv(uMVP, 1, false, mvp, 0);

        StarkPackage pkg = scene.pkg;
        StarkPackage.Step cur = current < pkg.steps.size() ? pkg.steps.get(current) : null;
        float pulse = 0.85f + 0.15f * (float) Math.sin(t * 4.0);

        // 1. Уже собранное: тускло
        for (GpuPart g : gpu) {
            int s = pkg.stepOf(g.partId);
            boolean done = s < current || (s < 0 && current > 0);
            if (cur == null) done = true;
            if (done) draw(g, cur == null ? 0.55f : 0.30f, null);
        }
        if (cur == null) return;
        // 2. Текущий шаг: ярко, с анимацией установки
        for (GpuPart g : gpu) {
            if (pkg.stepOf(g.partId) == current)
                draw(g, pulse, StepHud.insertOffset(cur.pathFor(g.partId), t));
        }
        // 3. Просвет: части текущего шага, закрытые корпусом, видны полупрозрачно
        GLES20.glDepthFunc(GLES20.GL_GREATER);
        GLES20.glDepthMask(false);
        for (GpuPart g : gpu) {
            if (pkg.stepOf(g.partId) == current)
                draw(g, 0.38f, StepHud.insertOffset(cur.pathFor(g.partId), t));
        }
        GLES20.glDepthMask(true);
        GLES20.glDepthFunc(GLES20.GL_LESS);
    }

    private void draw(GpuPart g, float level, float[] offset) {
        if (g.vbo == 0 || g.count == 0) return;
        GLES20.glUniform3f(uColor, GREEN[0] * level, GREEN[1] * level, GREEN[2] * level);
        if (offset == null) GLES20.glUniform3f(uOffset, 0, 0, 0);
        else GLES20.glUniform3f(uOffset, offset[0], offset[1], offset[2]);
        GLES20.glBindBuffer(GLES20.GL_ARRAY_BUFFER, g.vbo);
        GLES20.glEnableVertexAttribArray(aPos);
        GLES20.glVertexAttribPointer(aPos, 3, GLES20.GL_FLOAT, false, 24, 0);
        GLES20.glEnableVertexAttribArray(aNrm);
        GLES20.glVertexAttribPointer(aNrm, 3, GLES20.GL_FLOAT, false, 24, 12);
        GLES20.glDrawArrays(GLES20.GL_TRIANGLES, 0, g.count);
        GLES20.glBindBuffer(GLES20.GL_ARRAY_BUFFER, 0);
    }

    private void updateCamera(int current, float now) {
        GlbModel m = scene.model;
        float[] c = m.center();
        float r = m.radius();
        // Цель камеры: между центром изделия и деталями текущего шага
        float[] focus = c.clone();
        int n = 0;
        float fx = 0, fy = 0, fz = 0;
        for (GpuPart g : gpu)
            if (scene.pkg.stepOf(g.partId) == current && g.center != null) {
                fx += g.center[0];
                fy += g.center[1];
                fz += g.center[2];
                n++;
            }
        if (n > 0) {
            focus[0] = (c[0] + fx / n) / 2;
            focus[1] = (c[1] + fy / n) / 2;
            focus[2] = (c[2] + fz / n) / 2;
        }
        if (!camInit) {
            System.arraycopy(focus, 0, camTarget, 0, 3);
            camInit = true;
        }
        for (int i = 0; i < 3; i++) camTarget[i] += (focus[i] - camTarget[i]) * 0.08f;

        float aspect = vpW / (float) Math.max(1, vpH);
        float fovY = 32f;
        // Отступ, чтобы всё изделие помещалось и по узкой стороне
        float fit = r / (float) Math.sin(Math.toRadians(fovY / 2)) * 1.08f;
        if (aspect < 1) fit /= Math.max(0.55f, aspect);
        Matrix.perspectiveM(proj, 0, fovY, aspect, Math.max(0.001f, fit - r * 2.5f), fit + r * 3f);

        double yaw = Math.toRadians(35 + (autoRotate ? 28 * Math.sin(now * 0.45) : 0));
        double pitch = Math.toRadians(28);
        float ex = camTarget[0] + (float) (fit * Math.cos(pitch) * Math.sin(yaw));
        float ey = camTarget[1] + (float) (fit * Math.sin(pitch));
        float ez = camTarget[2] + (float) (fit * Math.cos(pitch) * Math.cos(yaw));
        Matrix.setLookAtM(view, 0, ex, ey, ez, camTarget[0], camTarget[1], camTarget[2], 0, 1, 0);
        Matrix.multiplyMM(mvp, 0, proj, 0, view, 0);

        // Свет из-за плеча наблюдателя
        float lx = ex - camTarget[0] + r, ly = ey - camTarget[1] + r * 2, lz = ez - camTarget[2];
        float len = (float) Math.sqrt(lx * lx + ly * ly + lz * lz);
        GLES20.glUniform3f(uLight, lx / len, ly / len, lz / len);
    }

    private void swapScene(Scene s) {
        for (GpuPart g : gpu) if (g.vbo != 0) GLES20.glDeleteBuffers(1, new int[]{g.vbo}, 0);
        gpu.clear();
        scene = s;
        camInit = false;
        for (GlbModel.PartMesh pm : s.model.parts.values()) {
            GpuPart g = new GpuPart();
            g.partId = pm.partId;
            g.count = pm.vertexCount();
            g.data = interleave(pm);
            g.center = centroid(pm.positions);
            g.vbo = upload(g.data);
            gpu.add(g);
        }
    }

    private static FloatBuffer interleave(GlbModel.PartMesh pm) {
        int v = pm.vertexCount();
        FloatBuffer fb = ByteBuffer.allocateDirect(v * 24).order(ByteOrder.nativeOrder()).asFloatBuffer();
        for (int i = 0; i < v; i++) {
            fb.put(pm.positions, i * 3, 3);
            fb.put(pm.normals, i * 3, 3);
        }
        fb.position(0);
        return fb;
    }

    private static float[] centroid(float[] p) {
        if (p.length == 0) return null;
        float[] mn = {Float.MAX_VALUE, Float.MAX_VALUE, Float.MAX_VALUE};
        float[] mx = {-Float.MAX_VALUE, -Float.MAX_VALUE, -Float.MAX_VALUE};
        for (int i = 0; i < p.length; i += 3)
            for (int k = 0; k < 3; k++) {
                mn[k] = Math.min(mn[k], p[i + k]);
                mx[k] = Math.max(mx[k], p[i + k]);
            }
        return new float[]{(mn[0] + mx[0]) / 2, (mn[1] + mx[1]) / 2, (mn[2] + mx[2]) / 2};
    }

    private static int upload(FloatBuffer data) {
        if (data == null || data.capacity() == 0) return 0;
        int[] id = new int[1];
        GLES20.glGenBuffers(1, id, 0);
        GLES20.glBindBuffer(GLES20.GL_ARRAY_BUFFER, id[0]);
        data.position(0);
        GLES20.glBufferData(GLES20.GL_ARRAY_BUFFER, data.capacity() * 4, data, GLES20.GL_STATIC_DRAW);
        GLES20.glBindBuffer(GLES20.GL_ARRAY_BUFFER, 0);
        return id[0];
    }

    private static int compile(int type, String src) {
        int s = GLES20.glCreateShader(type);
        GLES20.glShaderSource(s, src);
        GLES20.glCompileShader(s);
        int[] ok = new int[1];
        GLES20.glGetShaderiv(s, GLES20.GL_COMPILE_STATUS, ok, 0);
        if (ok[0] == 0) Log.e(TAG, "shader: " + GLES20.glGetShaderInfoLog(s));
        return s;
    }

    private static int link(int vs, int fs) {
        int p = GLES20.glCreateProgram();
        GLES20.glAttachShader(p, vs);
        GLES20.glAttachShader(p, fs);
        GLES20.glLinkProgram(p);
        int[] ok = new int[1];
        GLES20.glGetProgramiv(p, GLES20.GL_LINK_STATUS, ok, 0);
        if (ok[0] == 0) Log.e(TAG, "program: " + GLES20.glGetProgramInfoLog(p));
        return p;
    }
}
