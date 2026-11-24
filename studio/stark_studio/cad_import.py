"""Импорт STEP через OpenCascade (XCAF): дерево сборки, имена, цвета, матрицы, сетки."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from OCP.BRep import BRep_Tool
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib
from OCP.IFSelect import IFSelect_RetDone
from OCP.Quantity import Quantity_Color
from OCP.STEPCAFControl import STEPCAFControl_Reader
from OCP.TCollection import TCollection_ExtendedString
from OCP.TDataStd import TDataStd_Name
from OCP.TDF import TDF_Label, TDF_Tool
from OCP.TCollection import TCollection_AsciiString
from OCP.collections import Sequence_TDF_Label as TDF_LabelSequence
from OCP.TDocStd import TDocStd_Document
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.TopExp import TopExp_Explorer
from OCP.TopLoc import TopLoc_Location
from OCP.TopoDS import TopoDS
from OCP.XCAFApp import XCAFApp_Application
from OCP.XCAFDoc import (XCAFDoc_ColorGen, XCAFDoc_ColorSurf, XCAFDoc_DocumentTool)


@dataclass
class ShapeDef:
    """Уникальная геометрия (одна на все экземпляры, например все болты M6)."""
    key: str
    name: str
    vertices: np.ndarray  # (n,3) мм, локальные координаты
    faces: np.ndarray     # (m,3) int
    color: tuple[float, float, float, float]


@dataclass
class PartInstance:
    """Экземпляр детали в сборке."""
    id: str
    name: str
    shape_key: str
    transform: np.ndarray  # 4x4, мм, координаты CAD
    color: tuple[float, float, float, float]
    path: list[str] = field(default_factory=list)  # подсборки сверху вниз


@dataclass
class CadAssembly:
    name: str
    shapes: dict[str, ShapeDef]
    parts: list[PartInstance]


def _label_name(label: TDF_Label) -> str:
    attr = TDataStd_Name()
    if not label.FindAttribute(TDataStd_Name.GetID_s(), attr):
        return ""
    name = attr.Get().ToExtString()
    if name.startswith("=>"):  # автоимя компонента вида "=>[0:1:1:2]"
        return ""
    return fix_encoding(name)


def fix_encoding(name: str) -> str:
    """STEP часто хранит UTF-8 байты, которые читаются как Latin-1 (кириллица превращается в 'Ð...')."""
    try:
        fixed = name.encode("latin-1").decode("utf-8")
        return fixed
    except (UnicodeEncodeError, UnicodeDecodeError):
        return name


def _label_color(ct, label: TDF_Label):
    c = Quantity_Color()
    for kind in (XCAFDoc_ColorSurf, XCAFDoc_ColorGen):
        if ct.GetColor_s(label, kind, c):
            return (c.Red(), c.Green(), c.Blue(), 1.0)
    return None


def _trsf_to_np(loc: TopLoc_Location) -> np.ndarray:
    t = loc.Transformation()
    m = np.eye(4)
    for r in range(3):
        for c in range(4):
            m[r, c] = t.Value(r + 1, c + 1)
    return m


def _bbox_diag(shape) -> float:
    b = Bnd_Box()
    BRepBndLib.Add_s(shape, b)
    if b.IsVoid():
        return 1.0
    lo, hi = b.CornerMin(), b.CornerMax()
    return float(lo.Distance(hi))


def tessellate(shape, rel_deflection: float = 0.002, ang_deflection: float = 0.3):
    """Триангуляция формы. Прогиб относительно размера детали."""
    lin = max(0.01, _bbox_diag(shape) * rel_deflection)
    BRepMesh_IncrementalMesh(shape, lin, False, ang_deflection, True)
    verts, faces, offset = [], [], 0
    exp = TopExp_Explorer(shape, TopAbs_FACE)
    while exp.More():
        face = TopoDS.Face(exp.Current())
        loc = TopLoc_Location()
        tri = BRep_Tool.Triangulation_s(face, loc)
        if tri is not None:
            trsf = loc.Transformation()
            n = tri.NbNodes()
            pts = np.empty((n, 3))
            for i in range(1, n + 1):
                p = tri.Node(i).Transformed(trsf)
                pts[i - 1] = (p.X(), p.Y(), p.Z())
            tris = np.empty((tri.NbTriangles(), 3), dtype=np.int64)
            rev = face.Orientation() == TopAbs_REVERSED
            for i in range(1, tri.NbTriangles() + 1):
                a, b, c = tri.Triangle(i).Get()
                tris[i - 1] = (a, c, b) if rev else (a, b, c)
            verts.append(pts)
            faces.append(tris - 1 + offset)
            offset += n
        exp.Next()
    if not verts:
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64)
    return np.vstack(verts), np.vstack(faces)


DEFAULT_COLOR = (0.7, 0.7, 0.7, 1.0)


def read_step(path: str | Path) -> CadAssembly:
    path = Path(path)
    app = XCAFApp_Application.GetApplication_s()
    doc = TDocStd_Document(TCollection_ExtendedString("XmlXCAF"))
    app.InitDocument(doc)
    reader = STEPCAFControl_Reader()
    reader.SetNameMode(True)
    reader.SetColorMode(True)
    if reader.ReadFile(str(path)) != IFSelect_RetDone:
        raise ValueError(f"Не удалось прочитать STEP: {path.name}")
    if not reader.Transfer(doc):
        raise ValueError(f"Ошибка переноса STEP: {path.name}")

    st = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    ct = XCAFDoc_DocumentTool.ColorTool_s(doc.Main())

    shapes: dict[str, ShapeDef] = {}
    parts: list[PartInstance] = []
    shape_keys: dict[str, str] = {}

    def shape_for(ref: TDF_Label, inherited_color):
        e = TCollection_AsciiString()
        TDF_Tool.Entry_s(ref, e)
        entry = e.ToCString()
        if entry in shape_keys:
            return shape_keys[entry]
        key = f"G{len(shapes) + 1:03d}"
        shape = st.GetShape_s(ref)
        v, f = tessellate(shape)
        color = _label_color(ct, ref) or inherited_color or DEFAULT_COLOR
        shapes[key] = ShapeDef(key=key, name=_label_name(ref) or key, vertices=v, faces=f, color=color)
        shape_keys[entry] = key
        return key

    def walk(label: TDF_Label, trsf: np.ndarray, path_names: list[str], inst_name: str, inst_color):
        if st.IsAssembly_s(label):
            comps = TDF_LabelSequence()
            st.GetComponents_s(label, comps, False)
            for i in range(1, comps.Length() + 1):
                comp = comps.Value(i)
                loc = _trsf_to_np(st.GetLocation_s(comp))
                ref = TDF_Label()
                if st.GetReferredShape_s(comp, ref):
                    cname = _label_name(comp)
                    ccolor = _label_color(ct, comp) or inst_color
                    walk(ref, trsf @ loc, path_names + [_label_name(label)], cname, ccolor)
            return
        if not st.IsShape_s(label):
            return
        key = shape_for(label, inst_color)
        name = inst_name or _label_name(label) or key
        color = inst_color or shapes[key].color
        parts.append(PartInstance(id=f"P{len(parts) + 1:03d}", name=name, shape_key=key,
                                  transform=trsf, color=color, path=path_names))

    free = TDF_LabelSequence()
    st.GetFreeShapes(free)
    root_name = path.stem
    for i in range(1, free.Length() + 1):
        lab = free.Value(i)
        if i == 1 and _label_name(lab):
            root_name = _label_name(lab)
        walk(lab, np.eye(4), [], "", None)

    if not parts:
        raise ValueError("В STEP не найдено деталей")
    return CadAssembly(name=root_name, shapes=shapes, parts=parts)
