"""Тестовая STEP-сборка «Редукторный модуль» (16 деталей), построенная через OpenCascade.

Нужна, чтобы проверять конвейер без реальных CAD заказчика. Детали имеют зазоры
0.2 мм в отверстиях, как в нормальной CAD-модели с посадкой с зазором.
"""
from __future__ import annotations

from pathlib import Path

from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox, BRepPrimAPI_MakeCylinder
from OCP.gp import gp_Ax1, gp_Ax2, gp_Dir, gp_Pnt, gp_Trsf, gp_Vec
from OCP.IFSelect import IFSelect_RetDone
from OCP.Quantity import Quantity_Color, Quantity_TOC_RGB
from OCP.STEPCAFControl import STEPCAFControl_Writer
from OCP.STEPControl import STEPControl_AsIs
from OCP.TCollection import TCollection_ExtendedString
from OCP.TDataStd import TDataStd_Name
from OCP.TDocStd import TDocStd_Document
from OCP.TopLoc import TopLoc_Location
from OCP.XCAFApp import XCAFApp_Application
from OCP.XCAFDoc import XCAFDoc_ColorSurf, XCAFDoc_DocumentTool

CLEAR = 0.2  # радиальный зазор в отверстиях, мм


def _box(x0, y0, z0, dx, dy, dz):
    return BRepPrimAPI_MakeBox(gp_Pnt(x0, y0, z0), dx, dy, dz).Shape()


def _cyl(x, y, z, r, h, axis=(0, 0, 1)):
    return BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(x, y, z), gp_Dir(*axis)), r, h).Shape()


def _cut(shape, *tools):
    for t in tools:
        shape = BRepAlgoAPI_Cut(shape, t).Shape()
    return shape


BOLT_XY = [(sx * 45.0, sy * 31.5) for sx in (-1, 1) for sy in (-1, 1)]


def build_shapes():
    """Возвращает список (имя, форма, цвет, [трансформации]) в мм, ось Z вверх."""
    r6 = 3.0 + CLEAR  # отверстие под M6
    r_shaft = 6.0 + CLEAR

    # Основание 120x90x10, глухие отверстия под болты и вал
    base = _box(-60, -45, 0, 120, 90, 10)
    base = _cut(base, *[_cyl(x, y, 2, r6, 9) for x, y in BOLT_XY], _cyl(0, 0, 5, r_shaft, 6))

    # Подшипник (кольцо) на основании
    bearing = _cut(_cyl(0, 0, 10, 15, 8), _cyl(0, 0, 9, r_shaft, 10))

    # Вал
    shaft = _cyl(0, 0, 5, 6, 61)  # z 5..66

    # Корпус: рамка 100x70, внутр. 80x56, высота z 10..50, вертикальные отверстия под болты
    housing = _cut(_box(-50, -35, 10, 100, 70, 40), _box(-40, -28, 9, 80, 56, 42))
    housing = _cut(housing, *[_cyl(x, y, 9, r6, 42) for x, y in BOLT_XY])
    # Глухие горизонтальные отверстия под M4 для кронштейнов (±X)
    r4 = 2.0 + CLEAR
    for sx in (-1, 1):
        for y in (-8.0, 8.0):
            if sx > 0:
                housing = _cut(housing, _cyl(42, y, 30, r4, 9, axis=(1, 0, 0)))
            else:
                housing = _cut(housing, _cyl(-42, y, 30, r4, 9, axis=(-1, 0, 0)))

    # Шестерня на валу внутри корпуса
    gear = _cut(_cyl(0, 0, 30, 20, 10), _cyl(0, 0, 29, r_shaft, 12))

    # Крышка с отверстиями под болты и вал
    cover = _box(-50, -35, 50, 100, 70, 6)
    cover = _cut(cover, *[_cyl(x, y, 49, r6, 8) for x, y in BOLT_XY], _cyl(0, 0, 49, r_shaft, 8))

    # Маховик на конце вала
    knob = _cut(_cyl(0, 0, 56, 12, 10), _cyl(0, 0, 55, r_shaft, 12))

    # Болт M6 (локально: стержень вдоль +Z от 0, головка сверху)
    bolt6 = _cyl(0, 0, 0, 3.0, 54)  # z 0..54 → в сборке z 2.5..56.5? ставим смещение ниже
    head6 = _cyl(0, 0, 54, 5.0, 4)
    from OCP.BRepAlgoAPI import BRepAlgoAPI_Fuse
    bolt6 = BRepAlgoAPI_Fuse(bolt6, head6).Shape()

    # Кронштейн (локально для +X стороны): пластина x 50..56, y -15..15, z 20..40 с двумя отверстиями
    bracket = _box(50, -15, 20, 6, 30, 20)
    bracket = _cut(bracket, *[_cyl(49, y, 30, r4, 8, axis=(1, 0, 0)) for y in (-8.0, 8.0)])

    # Болт M4 (локально вдоль +X): стержень x 43..56, головка x 56..59
    bolt4 = BRepAlgoAPI_Fuse(_cyl(43, 0, 0, 2.0, 13, axis=(1, 0, 0)),
                             _cyl(56, 0, 0, 3.5, 3, axis=(1, 0, 0))).Shape()

    def tr(dx=0.0, dy=0.0, dz=0.0, rot_z_deg=0.0):
        t = gp_Trsf()
        if rot_z_deg:
            t.SetRotation(gp_Ax1(gp_Pnt(0, 0, 0), gp_Dir(0, 0, 1)), rot_z_deg * 3.141592653589793 / 180)
        t2 = gp_Trsf()
        t2.SetTranslation(gp_Vec(dx, dy, dz))
        return t2.Multiplied(t)

    grey, blue, light = (0.55, 0.57, 0.6), (0.2, 0.4, 0.75), (0.8, 0.82, 0.85)
    steel, black, yellow, orange, red = (0.7, 0.7, 0.72), (0.12, 0.12, 0.12), (0.9, 0.75, 0.2), (0.95, 0.5, 0.15), (0.8, 0.2, 0.2)

    return [
        ("Основание", base, grey, [tr()]),
        ("Подшипник 6201", bearing, steel, [tr()]),
        ("Вал", shaft, steel, [tr()]),
        ("Корпус", housing, blue, [tr()]),
        ("Шестерня Z40", gear, yellow, [tr()]),
        ("Крышка корпуса", cover, light, [tr()]),
        ("Маховик", knob, red, [tr()]),
        ("Болт M6x58 DIN 912", bolt6, black, [tr(x, y, 2.5) for x, y in BOLT_XY]),
        ("Кронштейн", bracket, orange, [tr(), tr(rot_z_deg=180)]),
        ("Болт M4x16 ISO 4762", bolt4, black,
         [tr(0, y, 30) for y in (-8.0, 8.0)] + [tr(0, y, 30, rot_z_deg=180) for y in (-8.0, 8.0)]),
    ]


def write_sample_step(path: str | Path) -> Path:
    path = Path(path)
    app = XCAFApp_Application.GetApplication_s()
    doc = TDocStd_Document(TCollection_ExtendedString("XmlXCAF"))
    app.InitDocument(doc)
    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())

    from OCP.TopoDS import TopoDS_Compound
    from OCP.BRep import BRep_Builder
    comp = TopoDS_Compound()
    BRep_Builder().MakeCompound(comp)
    assy = st.NewShape()
    TDataStd_Name.Set_s(assy, TCollection_ExtendedString("Редукторный модуль"))

    for name, shape, color, trsfs in build_shapes():
        lab = st.AddShape(shape, False)
        TDataStd_Name.Set_s(lab, TCollection_ExtendedString(name))
        ct.SetColor(lab, Quantity_Color(*color, Quantity_TOC_RGB), XCAFDoc_ColorSurf)
        for i, t in enumerate(trsfs):
            c = st.AddComponent(assy, lab, TopLoc_Location(t))
            if len(trsfs) > 1:
                TDataStd_Name.Set_s(c, TCollection_ExtendedString(f"{name}:{i + 1}"))
    st.UpdateAssemblies()

    w = STEPCAFControl_Writer()
    w.SetNameMode(True)
    w.SetColorMode(True)
    w.Transfer(doc, STEPControl_AsIs)
    status = w.Write(str(path))
    if status != IFSelect_RetDone:
        raise RuntimeError(f"STEP write failed: {status}")
    return path


if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else "gearbox_module.step"
    print(write_sample_step(out))
