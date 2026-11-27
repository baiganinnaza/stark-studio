package app.stark.rokid;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.opengl.GLSurfaceView;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import app.stark.rokid.core.GlbModel;
import app.stark.rokid.core.StarkPackage;
import app.stark.rokid.core.StepHud;
import app.stark.rokid.core.StudioLink;
import app.stark.rokid.core.TouchGesture;

/**
 * Stark для Rokid AI Glasses: пошаговая инструкция по сборке на монохромном экране очков.
 * Сверху номер шага и подсказка, в середине 3D-схема с подсветкой деталей шага, снизу детали, крепёж,
 * инструмент и проверки. Управление сенсором на дужке: свайп = шаг вперёд/назад, касание = повтор
 * анимации, долгое касание = меню. Пакеты: из Studio по Wi‑Fi (QR), кабелем (adb push) или образец.
 */
public final class MainActivity extends Activity implements LinkClient.Listener {
    private static final String TAG = "Stark";
    static final int REQ_QR = 1;

    // Монохромный зелёный экран: важна только яркость. Чёрный = прозрачный.
    static final int BRIGHT = Color.rgb(0x55, 0xFF, 0x77);
    static final int MID = Color.rgb(0x2E, 0xC0, 0x4E);
    static final int DIM = Color.rgb(0x1A, 0x80, 0x33);

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService loader = Executors.newSingleThreadExecutor();
    private PackageStore store;
    private LinkClient link;
    private ModelRenderer renderer;
    private GLSurfaceView glView;
    private View glArea;
    private TextView counter, headline, status, toast, loading;
    private LinearLayout lines, menuBox;

    private StarkPackage pkg;
    private String loadedSha;
    private PackageStore.Source source;
    private long cableSeen;
    private int step;
    private boolean show3d = true, reversed, linkOk;

    // меню
    private final List<String> menuLabels = new ArrayList<>();
    private final List<Runnable> menuActions = new ArrayList<>();
    private int menuSel = -1;

    // касание дужки
    private float downX, downY;
    private long downAt;
    private boolean touchDown, longFired;
    private long lastBack;
    private final Runnable longPress = () -> {
        longFired = true;
        onAction(TouchGesture.Action.LONG);
    };
    private final Runnable frame = new Runnable() {
        @Override
        public void run() {
            // ~30 кадров/с только пока видна 3D-схема (экономим батарею очков)
            if (glView != null && show3d && pkg != null && menuSel < 0) glView.requestRender();
            ui.postDelayed(this, 33);
        }
    };
    private final Runnable cableWatch = new Runnable() {
        @Override
        public void run() {
            checkCable();
            ui.postDelayed(this, 3000);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON | WindowManager.LayoutParams.FLAG_FULLSCREEN);
        store = new PackageStore(this);
        show3d = store.prefs().getBoolean("show3d", true);
        reversed = store.prefs().getBoolean("reversed", false);
        buildUi();
        link = new LinkClient(this, () -> loadedSha);
        PackageStore.Source cable = store.newestCable();
        cableSeen = cable == null ? 0 : cable.modified;
        handleIntent(getIntent());
        open(store.initial(), false);
    }

    // ---------- интерфейс ----------

    private TextView text(float sizeFraction, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setTextColor(color);
        t.setTypeface(Typeface.create("sans-serif-condensed", bold ? Typeface.BOLD : Typeface.NORMAL));
        t.setTag(sizeFraction);
        t.setIncludeFontPadding(false);
        return t;
    }

    private void buildUi() {
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.BLACK);

        renderer = new ModelRenderer();
        glView = new GLSurfaceView(this);
        glView.setEGLContextClientVersion(2);
        glView.setEGLConfigChooser(8, 8, 8, 8, 16, 0);
        glView.setPreserveEGLContextOnPause(true);
        glView.setRenderer(renderer);
        glView.setRenderMode(GLSurfaceView.RENDERMODE_WHEN_DIRTY);
        root.addView(glView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        counter = text(0.045f, MID, true);
        headline = text(0.068f, BRIGHT, true);
        headline.setLineSpacing(0, 1.05f);
        glArea = new View(this);
        lines = new LinearLayout(this);
        lines.setOrientation(LinearLayout.VERTICAL);
        status = text(0.036f, DIM, false);
        status.setSingleLine(true);
        col.addView(counter);
        col.addView(headline);
        col.addView(glArea, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));
        col.addView(lines);
        col.addView(status);
        root.addView(col, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        loading = text(0.06f, BRIGHT, true);
        loading.setGravity(Gravity.CENTER);
        root.addView(loading, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        toast = text(0.045f, BRIGHT, true);
        toast.setGravity(Gravity.CENTER);
        toast.setBackground(frameBg());
        toast.setVisibility(View.GONE);
        FrameLayout.LayoutParams tl = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER);
        root.addView(toast, tl);

        menuBox = new LinearLayout(this);
        menuBox.setOrientation(LinearLayout.VERTICAL);
        menuBox.setBackground(frameBg());
        menuBox.setVisibility(View.GONE);
        root.addView(menuBox, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER));

        setContentView(root);
        root.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> applySizes(r - l, b - t));
        // 3D рисуется только в средней полосе между текстом сверху и снизу
        glArea.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> renderer.setViewport(
                l, t + ((View) v.getParent()).getTop(), r - l, b - t, root.getHeight()));
        hideSystemUi();
    }

    private GradientDrawable frameBg() {
        GradientDrawable d = new GradientDrawable();
        d.setColor(Color.BLACK);
        d.setStroke(3, MID);
        d.setCornerRadius(10);
        return d;
    }

    /** Размер шрифта — доля ширины экрана: на Rokid экран маленький (≈480 px), на телефоне больше. */
    private void applySizes(int w, int h) {
        if (w <= 0) return;
        int pad = Math.max(8, w / 30);
        View col = (View) counter.getParent();
        col.setPadding(pad, pad, pad, pad / 2);
        int base = Math.min(w, (int) (h * 0.85f));
        for (TextView t : new TextView[]{counter, headline, status, loading, toast}) size(t, base);
        for (int i = 0; i < lines.getChildCount(); i++) size((TextView) lines.getChildAt(i), base);
        for (int i = 0; i < menuBox.getChildCount(); i++) size((TextView) menuBox.getChildAt(i), base);
        toast.setPadding(pad, pad / 2, pad, pad / 2);
        menuBox.setPadding(pad, pad, pad, pad);
    }

    private void size(TextView t, int base) {
        Object f = t.getTag();
        if (f instanceof Float) t.setTextSize(TypedValue.COMPLEX_UNIT_PX, base * (Float) f);
    }

    private void hideSystemUi() {
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
    }

    private void render() {
        if (pkg == null) return;
        StepHud hud = StepHud.of(pkg, step, "ru");
        String product = pkg.productName == null ? "" : "  ·  " + pkg.productName;
        counter.setText(hud.counter + product);
        headline.setText(hud.headline);
        lines.removeAllViews();
        int max = show3d ? 5 : 9;
        int shown = 0;
        for (String l : hud.lines) {
            if (shown == max) {
                TextView more = text(0.04f, DIM, false);
                more.setText("… ещё " + (hud.lines.size() - max));
                lines.addView(more);
                break;
            }
            TextView t = text(show3d ? 0.042f : 0.052f, l.startsWith("!") ? BRIGHT : MID, l.startsWith("!"));
            t.setText(l);
            t.setSingleLine(show3d);
            lines.addView(t);
            shown++;
        }
        glArea.setVisibility(View.VISIBLE);
        glView.setVisibility(show3d ? View.VISIBLE : View.INVISIBLE);
        renderer.setStep(step);
        View root = (View) glView.getParent();
        applySizes(root.getWidth(), root.getHeight());
        updateStatus(null);
    }

    private String linkText = "";

    private void updateStatus(String s) {
        if (s != null) linkText = s;
        String src = source == null ? "" : source.label;
        status.setText(linkText.isEmpty() ? src : linkText);
        status.setTextColor(linkOk || linkText.isEmpty() ? DIM : MID);
    }

    private void showToast(String s) {
        toast.setText(s);
        toast.setVisibility(View.VISIBLE);
        ui.removeCallbacks(hideToast);
        ui.postDelayed(hideToast, 2800);
    }

    private final Runnable hideToast = () -> toast.setVisibility(View.GONE);

    // ---------- пакеты ----------

    private void open(PackageStore.Source s, boolean announce) {
        loading.setText("Загрузка…");
        loading.setVisibility(pkg == null ? View.VISIBLE : View.GONE);
        loader.execute(() -> {
            try {
                byte[] data = store.read(s);
                load(s, data, announce);
            } catch (Exception e) {
                Log.e(TAG, "open " + s.key, e);
                ui.post(() -> failed(s, e));
            }
        });
    }

    /** Разбор пакета в фоне; модель на экране меняется только если пакет целиком прошёл проверку. */
    private void load(PackageStore.Source s, byte[] data, boolean announce) throws Exception {
        StarkPackage p = StarkPackage.load(data);
        Set<String> ids = new HashSet<>();
        for (StarkPackage.Part part : p.parts) ids.add(part.id);
        GlbModel m = GlbModel.parse(p.modelGlb, ids);
        String sha = StudioLink.sha256Hex(data);
        ui.post(() -> {
            boolean sameProduct = pkg != null && p.productId != null && p.productId.equals(pkg.productId);
            int keep = sameProduct ? step : store.savedStep(p.productId);
            pkg = p;
            loadedSha = sha;
            source = s;
            store.remember(s);
            step = Math.max(0, Math.min(keep, p.steps.size()));
            renderer.setScene(p, m);
            loading.setVisibility(View.GONE);
            render();
            if (announce) showToast("Открыт: " + (p.productName == null ? s.label : p.productName));
        });
    }

    private void failed(PackageStore.Source s, Exception e) {
        String msg = e.getMessage() == null ? e.toString() : e.getMessage();
        if (pkg == null) {
            if (s.key.startsWith("asset:")) {
                loading.setText("Не удалось открыть пакет:\n" + msg);
            } else {
                store.remember(new PackageStore.Source("asset:" + PackageStore.SAMPLE_ASSET, "Образец", 0));
                open(new PackageStore.Source("asset:" + PackageStore.SAMPLE_ASSET, "Образец", 0), false);
                showToast("Пакет не открылся: " + msg);
            }
        } else {
            showToast("Пакет не открылся: " + msg);
        }
    }

    /** Пакет положили кабелем (adb push) — открыть сразу. */
    private void checkCable() {
        PackageStore.Source c = store.newestCable();
        if (c != null && c.modified > cableSeen) {
            cableSeen = c.modified;
            open(c, true);
        }
    }

    @Override
    public void onLinkStatus(String s, boolean ok) {
        linkOk = ok;
        updateStatus(s);
    }

    @Override
    public void onLinkPackage(StudioLink.Remote r, byte[] data) {
        loader.execute(() -> {
            try {
                PackageStore.Source s = store.saveFromStudio(r, data);
                load(s, data, true);
            } catch (Exception e) {
                Log.e(TAG, "studio package", e);
                ui.post(() -> showToast("Пакет из Studio отклонён: " + e.getMessage()));
            }
        });
    }

    // ---------- шаги и ввод ----------

    private void go(int delta) {
        if (pkg == null) return;
        int n = pkg.steps.size();
        int next = Math.max(0, Math.min(n, step + delta));
        if (next == step) {
            showToast(delta > 0 ? "Это последний шаг" : "Это первый шаг");
            return;
        }
        step = next;
        store.saveStep(pkg.productId, step);
        render();
    }

    private void onAction(TouchGesture.Action a) {
        if (menuSel >= 0) {
            switch (a) {
                case NEXT:
                    menuMove(1);
                    break;
                case PREV:
                    menuMove(-1);
                    break;
                case TAP:
                    menuSelect();
                    break;
                case LONG:
                    closeMenu();
                    break;
                default:
                    break;
            }
            return;
        }
        switch (a) {
            case NEXT:
                go(1);
                break;
            case PREV:
                go(-1);
                break;
            case TAP:
                renderer.replay();
                break;
            case LONG:
                openMainMenu();
                break;
            default:
                break;
        }
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        int k = e.getKeyCode();
        boolean down = e.getAction() == KeyEvent.ACTION_DOWN;
        boolean up = e.getAction() == KeyEvent.ACTION_UP;
        switch (k) {
            case KeyEvent.KEYCODE_DPAD_RIGHT:
            case KeyEvent.KEYCODE_DPAD_DOWN:
            case KeyEvent.KEYCODE_PAGE_DOWN:
            case KeyEvent.KEYCODE_MEDIA_NEXT:
            case KeyEvent.KEYCODE_TAB:
                if (down) onAction(swap(TouchGesture.Action.NEXT, k));
                return true;
            case KeyEvent.KEYCODE_DPAD_LEFT:
            case KeyEvent.KEYCODE_DPAD_UP:
            case KeyEvent.KEYCODE_PAGE_UP:
            case KeyEvent.KEYCODE_MEDIA_PREVIOUS:
                if (down) onAction(swap(TouchGesture.Action.PREV, k));
                return true;
            case KeyEvent.KEYCODE_ENTER:
            case KeyEvent.KEYCODE_DPAD_CENTER:
            case KeyEvent.KEYCODE_SPACE:
            case KeyEvent.KEYCODE_BUTTON_A:
                if (down && e.getRepeatCount() == 0) {
                    longFired = false;
                    ui.postDelayed(longPress, TouchGesture.LONG_MIN_MS);
                } else if (up) {
                    ui.removeCallbacks(longPress);
                    if (!longFired) onAction(TouchGesture.Action.TAP);
                }
                return true;
            case KeyEvent.KEYCODE_MENU:
                if (up) onAction(TouchGesture.Action.LONG);
                return true;
            case KeyEvent.KEYCODE_BACK:
                if (up) onBackKey();
                return true;
            default:
                return super.dispatchKeyEvent(e);
        }
    }

    /** Направление клавиш дужки меняется вместе со свайпом; стрелки вверх/вниз в меню не трогаем. */
    private TouchGesture.Action swap(TouchGesture.Action a, int key) {
        boolean horizontal = key == KeyEvent.KEYCODE_DPAD_LEFT || key == KeyEvent.KEYCODE_DPAD_RIGHT;
        if (!reversed || !horizontal) return a;
        return a == TouchGesture.Action.NEXT ? TouchGesture.Action.PREV : TouchGesture.Action.NEXT;
    }

    private void onBackKey() {
        if (menuSel >= 0) {
            closeMenu();
            return;
        }
        long now = SystemClock.uptimeMillis();
        if (now - lastBack < 2000) {
            finish();
            return;
        }
        lastBack = now;
        showToast("Ещё раз «назад» — выход\nМеню — долгое касание");
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent e) {
        return touch(e) || super.dispatchTouchEvent(e);
    }

    @Override
    public boolean dispatchGenericMotionEvent(MotionEvent e) {
        if (e.isFromSource(InputDevice.SOURCE_TOUCHPAD) || e.isFromSource(InputDevice.SOURCE_MOUSE)) {
            if (touch(e)) return true;
        }
        return super.dispatchGenericMotionEvent(e);
    }

    /** Сенсор дужки (touchpad) или экран телефона: свайпы и касания. */
    private boolean touch(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downX = e.getX();
                downY = e.getY();
                downAt = e.getEventTime();
                touchDown = true;
                longFired = false;
                ui.postDelayed(longPress, TouchGesture.LONG_MIN_MS);
                return true;
            case MotionEvent.ACTION_MOVE:
                if (touchDown && Math.hypot(e.getX() - downX, e.getY() - downY) > swipePx(e) / 2)
                    ui.removeCallbacks(longPress);
                return true;
            case MotionEvent.ACTION_UP:
                if (!touchDown) return true;
                touchDown = false;
                ui.removeCallbacks(longPress);
                if (longFired) return true;
                TouchGesture.Action a = TouchGesture.classify(e.getX() - downX, e.getY() - downY,
                        e.getEventTime() - downAt, swipePx(e), reversed);
                if (a == TouchGesture.Action.LONG) a = TouchGesture.Action.NONE; // уже сработало по таймеру
                onAction(a);
                return true;
            case MotionEvent.ACTION_CANCEL:
                touchDown = false;
                ui.removeCallbacks(longPress);
                return true;
            default:
                return false;
        }
    }

    private float swipePx(MotionEvent e) {
        InputDevice d = e.getDevice();
        if (d != null && e.isFromSource(InputDevice.SOURCE_TOUCHPAD)) {
            InputDevice.MotionRange r = d.getMotionRange(MotionEvent.AXIS_X, InputDevice.SOURCE_TOUCHPAD);
            if (r != null && r.getRange() > 0) return r.getRange() * 0.12f;
        }
        return getResources().getDisplayMetrics().density * 40;
    }

    // ---------- меню ----------

    private void openMainMenu() {
        menuLabels.clear();
        menuActions.clear();
        add("Продолжить", this::closeMenu);
        add("Сканировать QR из Studio", () -> {
            closeMenu();
            startActivityForResult(new Intent(this, QrScanActivity.class), REQ_QR);
        });
        add("Открыть пакет…", this::openPackageMenu);
        add("Начать сборку сначала", () -> {
            closeMenu();
            step = 0;
            if (pkg != null) store.saveStep(pkg.productId, 0);
            render();
        });
        add("3D-схема: " + (show3d ? "вкл" : "выкл"), () -> {
            show3d = !show3d;
            store.prefs().edit().putBoolean("show3d", show3d).apply();
            closeMenu();
            render();
        });
        add("Свайп: " + (reversed ? "обратный" : "обычный"), () -> {
            reversed = !reversed;
            store.prefs().edit().putBoolean("reversed", reversed).apply();
            closeMenu();
            showToast(reversed ? "Свайп назад = следующий шаг" : "Свайп вперёд = следующий шаг");
        });
        StudioLink.Pairing p = store.pairing();
        if (p != null) add("Отключить Studio " + p.host(), () -> {
            store.savePairing(null);
            link.stop();
            linkOk = false;
            updateStatus("");
            closeMenu();
            showToast("Studio отключена");
        });
        showMenu(0);
    }

    private void openPackageMenu() {
        menuLabels.clear();
        menuActions.clear();
        add("← Назад", this::openMainMenu);
        for (PackageStore.Source s : store.list()) {
            String mark = source != null && source.key.equals(s.key) ? "✓ " : "";
            add(mark + s.label, () -> {
                closeMenu();
                open(s, true);
            });
        }
        showMenu(menuLabels.size() > 1 ? 1 : 0);
    }

    private void add(String label, Runnable r) {
        menuLabels.add(label);
        menuActions.add(r);
    }

    private void showMenu(int sel) {
        menuSel = sel;
        drawMenu();
        menuBox.setVisibility(View.VISIBLE);
    }

    private void drawMenu() {
        menuBox.removeAllViews();
        // На маленьком экране видно ~6 пунктов: показываем окно вокруг выбранного
        int window = 6;
        int from = Math.max(0, Math.min(menuSel - 2, menuLabels.size() - window));
        int to = Math.min(menuLabels.size(), from + window);
        for (int i = from; i < to; i++) {
            boolean sel = i == menuSel;
            TextView t = text(0.05f, sel ? BRIGHT : DIM, sel);
            t.setSingleLine(true);
            t.setText((sel ? "▶ " : "   ") + menuLabels.get(i));
            menuBox.addView(t);
        }
        TextView hint = text(0.036f, DIM, false);
        hint.setText("свайп — выбор · касание — ОК · назад — закрыть");
        menuBox.addView(hint);
        View root = (View) glView.getParent();
        applySizes(root.getWidth(), root.getHeight());
    }

    private void menuMove(int d) {
        int n = menuLabels.size();
        menuSel = ((menuSel + d) % n + n) % n;
        drawMenu();
    }

    private void menuSelect() {
        if (menuSel >= 0 && menuSel < menuActions.size()) menuActions.get(menuSel).run();
    }

    private void closeMenu() {
        menuSel = -1;
        menuBox.setVisibility(View.GONE);
    }

    // ---------- Studio по QR / ссылке ----------

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleIntent(intent);
    }

    private void handleIntent(Intent intent) {
        if (intent == null) return;
        Uri data = intent.getData();
        if (data != null && Intent.ACTION_VIEW.equals(intent.getAction())) pair(data.toString());
    }

    private void pair(String linkText) {
        StudioLink.Pairing p = StudioLink.parse(linkText);
        if (p == null) {
            showToast("Это не QR-код Stark Studio");
            return;
        }
        store.savePairing(p);
        link.start(p);
        showToast("Подключено к Studio " + p.host());
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_QR) {
            if (resultCode == RESULT_OK && data != null) pair(data.getStringExtra(QrScanActivity.EXTRA_TEXT));
            else if (data != null && data.getStringExtra(QrScanActivity.EXTRA_ERROR) != null)
                showToast(data.getStringExtra(QrScanActivity.EXTRA_ERROR));
        }
    }

    // ---------- жизненный цикл ----------

    @Override
    protected void onResume() {
        super.onResume();
        hideSystemUi();
        glView.onResume();
        ui.post(frame);
        ui.postDelayed(cableWatch, 1000);
        StudioLink.Pairing p = store.pairing();
        if (p != null) link.start(p);
    }

    @Override
    protected void onPause() {
        ui.removeCallbacks(frame);
        ui.removeCallbacks(cableWatch);
        ui.removeCallbacks(longPress);
        link.stop();
        glView.onPause();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        loader.shutdownNow();
        super.onDestroy();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemUi();
    }
}
