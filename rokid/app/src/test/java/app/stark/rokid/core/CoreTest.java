package app.stark.rokid.core;

import org.junit.Test;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.HashSet;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

import static org.junit.Assert.assertArrayEquals;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.fail;

/** Ядро плеера Rokid на образце samples/gearbox_module.stark (тот же пакет, что у Unity-плеера). */
public class CoreTest {

    static byte[] sample() throws Exception {
        File f = new File("../../samples/gearbox_module.stark");
        if (!f.exists()) f = new File("samples/gearbox_module.stark");
        if (!f.exists()) f = new File(System.getProperty("stark.sample", "../samples/gearbox_module.stark"));
        return Files.readAllBytes(f.toPath());
    }

    @Test
    public void loadsSamplePackage() throws Exception {
        StarkPackage p = StarkPackage.load(sample());
        assertEquals(1, p.formatVersion);
        assertEquals(17, p.parts.size());
        assertEquals(8, p.steps.size());
        assertNotNull(p.productName);
        assertNotNull(p.hint(p.steps.get(0), "ru"));
        // каждая деталь в одном шаге
        for (StarkPackage.Part part : p.parts) assertTrue(part.id, p.stepOf(part.id) >= 0);
    }

    @Test
    public void parsesGlbForEveryPart() throws Exception {
        StarkPackage p = StarkPackage.load(sample());
        HashSet<String> ids = new HashSet<>();
        for (StarkPackage.Part part : p.parts) ids.add(part.id);
        GlbModel m = GlbModel.parse(p.modelGlb, ids);
        assertEquals(17, m.parts.size());
        assertTrue(m.triangles > 1000);
        for (GlbModel.PartMesh pm : m.parts.values()) {
            assertEquals(0, pm.positions.length % 9);
            assertEquals(pm.positions.length, pm.normals.length);
        }
        // метры, Y вверх, стоит на столе
        assertEquals(0f, m.min[1], 1e-3f);
        assertTrue(m.radius() > 0.02f && m.radius() < 1f);
    }

    @Test
    public void hudShowsFastenersAndTorque() throws Exception {
        StarkPackage p = StarkPackage.load(sample());
        boolean torque = false;
        for (int i = 0; i < p.steps.size(); i++) {
            StepHud h = StepHud.of(p, i, "ru");
            assertEquals("Шаг " + (i + 1) + " из 8", h.counter);
            assertTrue(h.headline.length() <= StepHud.HEADLINE_MAX);
            for (String l : h.lines) {
                assertTrue(l, l.length() <= StepHud.LINE_MAX);
                if (l.equals("+ Болт M6x58 DIN 912 ×4")) torque = true;
            }
        }
        assertTrue("крепёж шага показан с количеством", torque);
        assertTrue(StepHud.of(p, 5, "ru").lines.contains("• Кронштейн ×2"));
        StepHud done = StepHud.of(p, 8, "ru");
        assertTrue(done.finished);
        assertEquals("Все 8 шагов выполнены", done.lines.get(0));
    }

    @Test
    public void clipsOnWordBoundary() {
        assertEquals("Установите крышку и…", StepHud.clip("Установите крышку и затяните болты", 21));
        assertEquals("коротко", StepHud.clip("коротко", 10));
        assertEquals("9,5", StepHud.torque(9.5));
        assertEquals("12", StepHud.torque(12.0));
    }

    @Test
    public void insertAnimationEndsInPlace() {
        float[][] path = {{0, 0.06f, 0}, {0, 0, 0}};
        assertArrayEquals(new float[]{0, 0.06f, 0}, StepHud.insertOffset(path, 0f), 1e-6f);
        assertArrayEquals(new float[]{0, 0, 0}, StepHud.insertOffset(path, 2.0f), 1e-6f);
        assertArrayEquals(new float[]{0, 0, 0}, StepHud.insertOffset(null, 1f), 0f);
    }

    @Test
    public void rejectsBrokenPackages() throws Exception {
        try {
            StarkPackage.load("not a zip".getBytes(StandardCharsets.UTF_8));
            fail();
        } catch (StarkFormatException e) {
            assertTrue(e.getMessage().contains("не zip"));
        }
        try {
            StarkPackage.load(zip("manifest.json", "{\"formatVersion\":2}"));
            fail();
        } catch (StarkFormatException e) {
            assertTrue(e.getMessage(), e.getMessage().contains("Обновите"));
        }
    }

    static byte[] zip(String name, String content) throws Exception {
        ByteArrayOutputStream bo = new ByteArrayOutputStream();
        try (ZipOutputStream z = new ZipOutputStream(bo)) {
            z.putNextEntry(new ZipEntry(name));
            z.write(content.getBytes(StandardCharsets.UTF_8));
            z.closeEntry();
        }
        return bo.toByteArray();
    }

    @Test
    public void parsesStudioLinks() {
        String token = "AbCdEfGhIjKlMnOpQrStUvWx";
        StudioLink.Pairing a = StudioLink.parse("stark://link?u=http%3A%2F%2F192.168.1.10%3A8041&t=" + token);
        assertNotNull(a);
        assertEquals("http://192.168.1.10:8041", a.baseUrl);
        assertEquals(token, a.token);
        assertEquals("192.168.1.10:8041", a.host());
        StudioLink.Pairing b = StudioLink.parse("http://192.168.1.10:8041/s/" + token);
        assertNotNull(b);
        assertEquals("http://192.168.1.10:8041/api/v1/current.stark", b.packageUrl());
        assertNull(StudioLink.parse("http://192.168.1.10:8041/s/short"));
        assertNull(StudioLink.parse("stark://link?u=http%3A%2F%2Fh%2Fpath&t=" + token));
        assertNull(StudioLink.parse("stark://link?u=file%3A%2F%2F%2Fetc&t=" + token));
        assertNull(StudioLink.parse("https://example.com/"));
        assertNull(StudioLink.parse("hello"));
    }

    @Test
    public void parsesCurrentAndVerifies() throws Exception {
        assertNull(StudioLink.parseCurrent("{\"app\":\"stark-studio\",\"protocol\":1,\"package\":null}"));
        byte[] data = sample();
        String sha = StudioLink.sha256Hex(data);
        StudioLink.Remote r = StudioLink.parseCurrent("{\"protocol\":1,\"package\":{\"id\":\"gear/box 1\",\"name\":\"Редуктор\",\"version\":3,"
                + "\"sha256\":\"" + sha.toUpperCase() + "\",\"size\":" + data.length + ",\"steps\":8}}");
        assertEquals(3, r.version);
        assertTrue(StudioLink.isNew(r, null));
        assertFalse(StudioLink.isNew(r, sha));
        StudioLink.verify(r, data);
        assertEquals("gear-box-1.stark", StudioLink.fileNameFor(r));
        try {
            StudioLink.verify(r, new byte[]{1, 2, 3});
            fail();
        } catch (StarkFormatException e) {
            assertTrue(e.getMessage().contains("повреждённым"));
        }
        try {
            StudioLink.parseCurrent("{\"protocol\":2,\"package\":null}");
            fail();
        } catch (StarkFormatException e) {
            assertTrue(e.getMessage().contains("Обновите"));
        }
        assertEquals(3000, StudioLink.pollDelayMs(0));
        assertEquals(30000, StudioLink.pollDelayMs(10));
    }

    @Test
    public void touchpadGestures() {
        assertEquals(TouchGesture.Action.NEXT, TouchGesture.classify(80, 5, 200, 50, false));
        assertEquals(TouchGesture.Action.PREV, TouchGesture.classify(-80, 5, 200, 50, false));
        assertEquals(TouchGesture.Action.PREV, TouchGesture.classify(80, 5, 200, 50, true));
        assertEquals(TouchGesture.Action.NEXT, TouchGesture.classify(3, 70, 200, 50, false));
        assertEquals(TouchGesture.Action.TAP, TouchGesture.classify(3, 2, 150, 50, false));
        assertEquals(TouchGesture.Action.LONG, TouchGesture.classify(3, 2, 900, 50, false));
        assertEquals(TouchGesture.Action.NONE, TouchGesture.classify(30, 0, 200, 50, false));
    }
}
