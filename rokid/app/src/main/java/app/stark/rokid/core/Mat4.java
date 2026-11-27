package app.stark.rokid.core;

/** Матрицы 4×4 по столбцам (как glTF и OpenGL), без зависимостей от Android. */
public final class Mat4 {
    private Mat4() {
    }

    public static float[] identity() {
        return new float[]{1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
    }

    public static float[] mul(float[] a, float[] b) {
        float[] r = new float[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++) {
                float s = 0;
                for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[c * 4 + k];
                r[c * 4 + row] = s;
            }
        return r;
    }

    /** translation, rotation (кватернион x,y,z,w), scale → матрица. */
    public static float[] trs(float[] t, float[] q, float[] s) {
        float x = q[0], y = q[1], z = q[2], w = q[3];
        float[] r = new float[16];
        r[0] = (1 - 2 * (y * y + z * z)) * s[0];
        r[1] = (2 * (x * y + z * w)) * s[0];
        r[2] = (2 * (x * z - y * w)) * s[0];
        r[4] = (2 * (x * y - z * w)) * s[1];
        r[5] = (1 - 2 * (x * x + z * z)) * s[1];
        r[6] = (2 * (y * z + x * w)) * s[1];
        r[8] = (2 * (x * z + y * w)) * s[2];
        r[9] = (2 * (y * z - x * w)) * s[2];
        r[10] = (1 - 2 * (x * x + y * y)) * s[2];
        r[12] = t[0];
        r[13] = t[1];
        r[14] = t[2];
        r[15] = 1;
        return r;
    }
}
