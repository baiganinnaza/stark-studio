package app.stark.rokid.core;

/**
 * Жест на сенсорной дужке очков → команда плеера. Без Android, чтобы проверять тестами.
 * Свайп вперёд/вниз = следующий шаг, назад/вверх = предыдущий, короткое касание = повтор анимации,
 * долгое касание = меню. Направление свайпа можно поменять в меню (прошивки отдают ось по-разному).
 */
public final class TouchGesture {
    public enum Action {NONE, NEXT, PREV, TAP, LONG}

    public static final long TAP_MAX_MS = 400;
    public static final long LONG_MIN_MS = 650;

    private TouchGesture() {
    }

    /**
     * @param dx       смещение по X за касание, px (положительное = вправо/вперёд)
     * @param dy       смещение по Y, px (положительное = вниз)
     * @param durationMs длительность касания
     * @param swipePx  минимальная длина свайпа
     * @param reversed поменять направление свайпа
     */
    public static Action classify(float dx, float dy, long durationMs, float swipePx, boolean reversed) {
        float ax = Math.abs(dx), ay = Math.abs(dy);
        if (Math.max(ax, ay) >= swipePx) {
            boolean forward = ax >= ay ? dx > 0 : dy > 0;
            if (reversed) forward = !forward;
            return forward ? Action.NEXT : Action.PREV;
        }
        if (Math.max(ax, ay) < swipePx / 2) {
            if (durationMs >= LONG_MIN_MS) return Action.LONG;
            if (durationMs <= TAP_MAX_MS) return Action.TAP;
        }
        return Action.NONE;
    }
}
