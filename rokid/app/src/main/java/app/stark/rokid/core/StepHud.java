package app.stark.rokid.core;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Что показать на монохромном экране очков для шага: короткий заголовок и строки деталей.
 * Без Android, чтобы проверять тестами формулировки и обрезку длинного текста.
 */
public final class StepHud {
    /** Сколько символов влезает в строку крупного шрифта на экране Rokid (оценка, режем по словам). */
    public static final int HEADLINE_MAX = 80;
    public static final int LINE_MAX = 46;

    public final String counter;
    public final String headline;
    public final List<String> lines;
    public final boolean finished;

    private StepHud(String counter, String headline, List<String> lines, boolean finished) {
        this.counter = counter;
        this.headline = headline;
        this.lines = lines;
        this.finished = finished;
    }

    /** index == steps.size() — экран «Сборка завершена». */
    public static StepHud of(StarkPackage pkg, int index, String lang) {
        int n = pkg.steps.size();
        if (index >= n) {
            List<String> l = new ArrayList<>();
            l.add("Все " + n + " " + plural(n, "шаг", "шага", "шагов") + " выполнены");
            l.add("Свайп назад — вернуться к шагам");
            return new StepHud("Готово", "Сборка завершена", l, true);
        }
        StarkPackage.Step s = pkg.steps.get(Math.max(0, index));
        String headline = pkg.hint(s, lang);
        if (headline == null || headline.trim().isEmpty()) headline = s.title;
        if (headline == null || headline.trim().isEmpty()) headline = "Шаг " + (s.index + 1);
        headline = clip(headline.trim(), HEADLINE_MAX);

        List<String> lines = new ArrayList<>();
        if (s.review != null) lines.add(clip("! Проверить: " + s.review, LINE_MAX));

        Map<String, Integer> main = new LinkedHashMap<>();
        for (String id : s.parts) {
            String name = baseName(pkg.partName(id));
            Integer c = main.get(name);
            main.put(name, c == null ? 1 : c + 1);
        }
        for (Map.Entry<String, Integer> e : main.entrySet())
            lines.add(clip("• " + e.getKey() + (e.getValue() > 1 ? " ×" + e.getValue() : ""), LINE_MAX));
        for (StarkPackage.Fastener f : s.fasteners) {
            String name = f.name != null ? f.name : (f.partIds.isEmpty() ? "Крепёж" : pkg.partName(f.partIds.get(0)));
            int qty = f.qty > 0 ? f.qty : f.partIds.size();
            StringBuilder sb = new StringBuilder("+ ").append(name);
            if (qty > 1) sb.append(" ×").append(qty);
            if (f.torqueNm != null) sb.append(" · ").append(torque(f.torqueNm)).append(" Н·м");
            lines.add(clip(sb.toString(), LINE_MAX));
        }
        if (s.tool != null && !s.tool.trim().isEmpty()) lines.add(clip("Инструмент: " + s.tool.trim(), LINE_MAX));
        for (String c : s.checks) if (c != null && !c.trim().isEmpty()) lines.add(clip("– " + c.trim(), LINE_MAX));
        return new StepHud("Шаг " + (s.index + 1) + " из " + n, headline, lines, false);
    }

    /** «Кронштейн:2» → «Кронштейн»: экземпляры одной детали показываются одной строкой с ×N. */
    static String baseName(String name) {
        return name.replaceFirst(":\\d+$", "");
    }

    static String torque(double nm) {
        if (Math.abs(nm - Math.rint(nm)) < 1e-9) return String.valueOf((long) Math.rint(nm));
        return String.format(Locale.ROOT, "%.1f", nm).replace('.', ',');
    }

    /** Обрезать по границе слова с «…». */
    public static String clip(String s, int max) {
        if (s.length() <= max) return s;
        int cut = s.lastIndexOf(' ', max - 1);
        if (cut < max / 2) cut = max - 1;
        return s.substring(0, cut).trim() + "…";
    }

    static String plural(int n, String one, String few, String many) {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
        return many;
    }

    /**
     * Смещение детали на пути установки в момент t (с): путь проигрывается за 1,6 с с плавным началом и
     * концом, потом деталь 0,8 с стоит на месте, и цикл повторяется. path == null → на месте.
     */
    public static float[] insertOffset(float[][] path, float t) {
        if (path == null || path.length == 0) return new float[]{0, 0, 0};
        if (path.length == 1) return path[0].clone();
        final float move = 1.6f, cycle = 2.4f;
        float u = (t % cycle) / move;
        if (u > 1) u = 1;
        u = u * u * (3 - 2 * u);
        float x = u * (path.length - 1);
        int i = Math.min((int) x, path.length - 2);
        float f = x - i;
        float[] a = path[i], b = path[i + 1];
        return new float[]{a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f};
    }
}
