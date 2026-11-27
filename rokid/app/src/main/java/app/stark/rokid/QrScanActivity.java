package app.stark.rokid;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.graphics.Typeface;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Size;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.WindowManager;
import android.widget.TextView;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.NotFoundException;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.HybridBinarizer;

import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.EnumMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicBoolean;

import app.stark.rokid.core.StudioLink;

/**
 * Сканирование QR-кода Stark Studio камерой очков. Превью не нужно: экран прозрачный, сборщик просто
 * смотрит на QR на мониторе. Кадры YUV → zxing, первая ссылка Stark возвращается в MainActivity.
 */
public final class QrScanActivity extends Activity {
    static final String EXTRA_TEXT = "text";
    static final String EXTRA_ERROR = "error";
    private static final String TAG = "StarkQr";
    private static final int REQ_CAMERA = 7;
    private static final long TIMEOUT_MS = 90_000;

    private TextView label;
    private HandlerThread camThread;
    private Handler cam;
    private CameraDevice device;
    private CameraCaptureSession session;
    private ImageReader reader;
    private final AtomicBoolean busy = new AtomicBoolean();
    private final AtomicBoolean done = new AtomicBoolean();
    private final MultiFormatReader zxing = new MultiFormatReader();
    private final Handler ui = new Handler();
    private long lastWrongHint;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON | WindowManager.LayoutParams.FLAG_FULLSCREEN);
        label = new TextView(this);
        label.setBackgroundColor(Color.BLACK);
        label.setTextColor(MainActivity.BRIGHT);
        label.setTypeface(Typeface.create("sans-serif-condensed", Typeface.BOLD));
        label.setGravity(Gravity.CENTER);
        int w = getResources().getDisplayMetrics().widthPixels;
        label.setTextSize(TypedValue.COMPLEX_UNIT_PX, w * 0.06f);
        label.setPadding(w / 20, 0, w / 20, 0);
        label.setText("Посмотрите на QR-код\nв Stark Studio\n(кнопка «QR»)\n\nназад — отмена");
        setContentView(label);

        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        zxing.setHints(hints);
        ui.postDelayed(() -> fail("QR-код не найден. Попробуйте ближе к экрану"), TIMEOUT_MS);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_CAMERA);
        } else {
            startCamera();
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        if (requestCode != REQ_CAMERA) return;
        if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) startCamera();
        else fail("Нет доступа к камере очков");
    }

    @Override
    protected void onPause() {
        stopCamera();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        ui.removeCallbacksAndMessages(null);
        super.onDestroy();
    }

    private void startCamera() {
        if (device != null || done.get()) return;
        camThread = new HandlerThread("stark-qr");
        camThread.start();
        cam = new Handler(camThread.getLooper());
        CameraManager cm = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
        try {
            String id = pickCamera(cm);
            if (id == null) {
                fail("У очков не найдена камера");
                return;
            }
            Size size = pickSize(cm.getCameraCharacteristics(id));
            reader = ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 2);
            reader.setOnImageAvailableListener(this::onImage, cam);
            //noinspection MissingPermission — проверено в onResume
            cm.openCamera(id, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice d) {
                    device = d;
                    try {
                        //noinspection deprecation — API 26 совместимый вариант
                        d.createCaptureSession(Collections.singletonList(reader.getSurface()), new CameraCaptureSession.StateCallback() {
                            @Override
                            public void onConfigured(CameraCaptureSession s) {
                                session = s;
                                try {
                                    CaptureRequest.Builder b = d.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                                    b.addTarget(reader.getSurface());
                                    b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                                    s.setRepeatingRequest(b.build(), null, cam);
                                } catch (CameraAccessException | IllegalStateException e) {
                                    fail("Камера занята: " + e.getMessage());
                                }
                            }

                            @Override
                            public void onConfigureFailed(CameraCaptureSession s) {
                                fail("Камера не запустилась");
                            }
                        }, cam);
                    } catch (CameraAccessException e) {
                        fail("Камера занята: " + e.getMessage());
                    }
                }

                @Override
                public void onDisconnected(CameraDevice d) {
                    d.close();
                    device = null;
                }

                @Override
                public void onError(CameraDevice d, int error) {
                    d.close();
                    device = null;
                    fail("Камера занята другим приложением (код " + error + ")");
                }
            }, cam);
        } catch (CameraAccessException | SecurityException | IllegalArgumentException e) {
            Log.e(TAG, "camera", e);
            fail("Камера недоступна: " + e.getMessage());
        }
    }

    private static String pickCamera(CameraManager cm) throws CameraAccessException {
        String[] ids = cm.getCameraIdList();
        for (String id : ids) {
            Integer f = cm.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING);
            if (f != null && f == CameraCharacteristics.LENS_FACING_BACK) return id;
        }
        return ids.length > 0 ? ids[0] : null;
    }

    /** Около 1280×720: QR на мониторе с расстояния ~1 м ещё читается, а кадр обрабатывается быстро. */
    private static Size pickSize(CameraCharacteristics c) {
        StreamConfigurationMap map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Size best = new Size(640, 480);
        if (map == null) return best;
        Size[] sizes = map.getOutputSizes(ImageFormat.YUV_420_888);
        if (sizes == null || sizes.length == 0) return best;
        long target = 1280L * 720;
        long bestDiff = Long.MAX_VALUE;
        for (Size s : sizes) {
            long diff = Math.abs((long) s.getWidth() * s.getHeight() - target);
            if (diff < bestDiff) {
                bestDiff = diff;
                best = s;
            }
        }
        return best;
    }

    private void onImage(ImageReader r) {
        Image img = r.acquireLatestImage();
        if (img == null) return;
        if (done.get() || !busy.compareAndSet(false, true)) {
            img.close();
            return;
        }
        int w = img.getWidth(), h = img.getHeight();
        byte[] y;
        try {
            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer buf = plane.getBuffer();
            int rowStride = plane.getRowStride();
            int pixelStride = plane.getPixelStride();
            y = new byte[w * h];
            if (pixelStride == 1 && rowStride == w) {
                buf.get(y, 0, Math.min(y.length, buf.remaining()));
            } else {
                byte[] row = new byte[rowStride];
                for (int j = 0; j < h; j++) {
                    buf.position(j * rowStride);
                    int len = Math.min(rowStride, buf.remaining());
                    buf.get(row, 0, len);
                    for (int i = 0; i < w && i * pixelStride < len; i++) y[j * w + i] = row[i * pixelStride];
                }
            }
        } finally {
            img.close();
        }
        try {
            Result res = zxing.decodeWithState(new BinaryBitmap(new HybridBinarizer(
                    new PlanarYUVLuminanceSource(y, w, h, 0, 0, w, h, false))));
            String text = res.getText();
            if (StudioLink.parse(text) != null) {
                if (done.compareAndSet(false, true)) ui.post(() -> {
                    setResult(RESULT_OK, new Intent().putExtra(EXTRA_TEXT, text));
                    finish();
                });
            } else {
                long now = System.currentTimeMillis();
                if (now - lastWrongHint > 3000) {
                    lastWrongHint = now;
                    ui.post(() -> label.setText("Это не QR-код Stark Studio.\nОткройте «QR» в Studio\n\nназад — отмена"));
                }
            }
        } catch (NotFoundException ignored) {
            // в кадре нет QR
        } catch (RuntimeException e) {
            Log.w(TAG, "decode", e);
        } finally {
            zxing.reset();
            busy.set(false);
        }
    }

    private void fail(String message) {
        if (!done.compareAndSet(false, true)) return;
        ui.post(() -> {
            setResult(RESULT_CANCELED, new Intent().putExtra(EXTRA_ERROR, message));
            finish();
        });
    }

    private void stopCamera() {
        try {
            if (session != null) session.close();
        } catch (RuntimeException ignored) {
        }
        session = null;
        if (device != null) device.close();
        device = null;
        if (reader != null) reader.close();
        reader = null;
        if (camThread != null) camThread.quitSafely();
        camThread = null;
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        if (e.getKeyCode() == KeyEvent.KEYCODE_BACK
                || e.getKeyCode() == KeyEvent.KEYCODE_ENTER || e.getKeyCode() == KeyEvent.KEYCODE_DPAD_CENTER) {
            if (e.getAction() == KeyEvent.ACTION_UP) {
                done.set(true);
                setResult(RESULT_CANCELED);
                finish();
            }
            return true;
        }
        return super.dispatchKeyEvent(e);
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent e) {
        return true; // касания дужки не должны ничего нажимать во время сканирования
    }
}
