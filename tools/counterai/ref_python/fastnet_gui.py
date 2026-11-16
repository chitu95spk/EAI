#!/usr/bin/env python
"""Giao dien Qt5: kiem tra du lieu, huan luyen FastNet, chuyen sang OpenVINO, thu dem nhanh.

Chay:   python fastnet_gui.py

Sau tab:
    1. Du lieu      chon thu muc anh + nhan (*_points.csv), kiem tra chat luong nhan
    2. Dan nhan     danh sach anh, danh dau san bang FastNet/Cellpose, them/xoa bang chuot, luu CSV
    3. Huan luyen   dat tham so, bam Bat dau, xem duong loss + ket qua kiem tra, dat lam fastnet.pt
    4. OpenVINO     chuyen .pt -> IR (FP16/FP32) va do toc do tren CPU / iGPU so voi PyTorch
    5. Thu nhanh    dem 1 anh bang .pt hoac .xml, xem anh ket qua + thoi gian
    6. Dem hang loat dem ca thu muc, bang + thong ke do on dinh, so voi so dung, luu anh, xuat CSV

Can: pip install PyQt5   (cung voi torch, opencv-python, openvino)
"""
import csv
import os
import sys
import time
import shutil
import traceback
from pathlib import Path

import torch  # noqa: F401  -- phai import TRUOC PyQt5 tren Windows (neu khong torch loi DLL 1114)
import cv2
import numpy as np
from PyQt5 import QtCore, QtGui, QtWidgets
from PyQt5.QtCore import Qt, QThread, QSettings, pyqtSignal

import fast_counter as fc
import fastnet_openvino as fo
import label_points as lp
import train_fastnet as tf
from app_paths import FROZEN, app_dir

HERE = app_dir(__file__)                         # canh file .exe khi da dong goi
ROOT = (HERE / "samples") if FROZEN else HERE.parent   # thu muc anh mac dinh
MODELS = HERE / "models"

if sys.stdout is None or sys.stderr is None:     # ban .exe khong co cua so console: ghi log ra file canh .exe
    try:
        _log = open(HERE / "fastnet_gui.log", "a", encoding="utf-8", buffering=1)
    except OSError:
        _log = open(os.devnull, "w")
    sys.stdout = sys.stdout or _log
    sys.stderr = sys.stderr or _log


def default_settings():
    """Ban dong goi: settings.ini canh .exe (mang theo duoc, khong ghi registry); neu khong ghi duoc thi dung registry."""
    if FROZEN and os.access(HERE, os.W_OK):
        return QSettings(str(HERE / "settings.ini"), QSettings.IniFormat)
    return QSettings("Coil_Nisei", "FastNetGUI")


def existing(path, default, want_dir=False):
    """Duong dan da luu neu con ton tai, nguoc lai mac dinh (vd thu muc ung dung bi chuyen cho)."""
    p = Path(str(path)) if path else None
    return str(path) if p and (p.is_dir() if want_dir else p.is_file()) else str(default)


# --------------------------------------------------------------------------- tien ich
class Worker(QThread):
    """Chay mot ham nang trong luong rieng. fn(worker) -> ket qua; ham goi worker.log.emit(...)."""
    log = pyqtSignal(str)
    status = pyqtSignal(str)
    item = pyqtSignal(object)                    # ket qua tung muc (vd dem hang loat)
    progress = pyqtSignal(int, int, float)
    done = pyqtSignal(object)
    failed = pyqtSignal(str)
    _active = set()                              # moi luong dang chay, de closeEvent biet phai doi

    def __init__(self, fn, parent=None, quiet=False):
        super().__init__(parent)
        self.fn = fn
        self.quiet = quiet                       # tac vu ngan (kiem tra du lieu, danh dau san): dong cua so khong can hoi
        self._stop = False

    def start(self, *args):
        Worker._active.add(self)
        self.finished.connect(lambda: Worker._active.discard(self))
        super().start(*args)

    @staticmethod
    def running():
        return [w for w in list(Worker._active) if w.isRunning()]

    def run(self):
        try:
            self.done.emit(self.fn(self))
        except BaseException:                    # ke ca SystemExit: ham nap mo hinh goi sys.exit() khi thieu file
            self.failed.emit(traceback.format_exc())

    def stop(self):
        self._stop = True

    def should_stop(self):
        return self._stop


def sget(settings, key, default, cast=str):
    """Doc 1 gia tri QSettings, ep kieu; sai kieu/thieu -> mac dinh."""
    v = settings.value(key, None)
    if v is None:
        return default
    try:
        return (str(v).lower() == "true") if cast is bool else cast(v)
    except (TypeError, ValueError):
        return default


def path_row(edit, mode, parent, filt="", save=False):
    """O nhap duong dan + nut Chon... Tra ve widget hang ngang."""
    box = QtWidgets.QWidget()
    lay = QtWidgets.QHBoxLayout(box)
    lay.setContentsMargins(0, 0, 0, 0)
    btn = QtWidgets.QPushButton("Chọn…")

    def pick():
        start = edit.text() or str(ROOT)
        if mode == "dir":
            p = QtWidgets.QFileDialog.getExistingDirectory(parent, "Chọn thư mục", start)
        elif save:
            p, _ = QtWidgets.QFileDialog.getSaveFileName(parent, "Lưu thành", start, filt)
        else:
            p, _ = QtWidgets.QFileDialog.getOpenFileName(parent, "Chọn file", start, filt)
        if p:
            edit.setText(p)

    btn.clicked.connect(pick)
    lay.addWidget(edit, 1)
    lay.addWidget(btn)
    return box


def log_box():
    w = QtWidgets.QPlainTextEdit()
    w.setReadOnly(True)
    w.setFont(QtGui.QFontDatabase.systemFont(QtGui.QFontDatabase.FixedFont))
    w.setMaximumBlockCount(5000)
    return w


def to_pixmap(bgr):
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    h, w = rgb.shape[:2]
    return QtGui.QPixmap.fromImage(QtGui.QImage(rgb.data, w, h, 3 * w, QtGui.QImage.Format_RGB888).copy())


class ImageView(QtWidgets.QLabel):
    """Hien anh vua khung, giu ti le."""

    def __init__(self):
        super().__init__("Chưa có ảnh")
        self.setAlignment(Qt.AlignCenter)
        self.setMinimumSize(320, 240)
        self.setSizePolicy(QtWidgets.QSizePolicy.Ignored, QtWidgets.QSizePolicy.Ignored)
        self.setStyleSheet("background:#202020; color:#aaa;")
        self._pix = None

    def set_image(self, bgr):
        self._pix = to_pixmap(bgr)
        self._fit()

    def _fit(self):
        if self._pix is not None:
            self.setPixmap(self._pix.scaled(self.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation))

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._fit()


def smooth_curve(vals):
    """(duong tho, duong lam mem) cung do dai; giam diem neu qua nhieu."""
    v = np.array(vals, np.float64)
    if len(v) > 1500:                            # giam diem ve, tranh chi phi O(n^2) khi huan luyen dai
        b = int(np.ceil(len(v) / 1000))
        v = v[:len(v) // b * b].reshape(-1, b).mean(1)
    k = max(len(v) // 40, 1)
    if k > 1:                                    # trung binh truot, dem bien bang gia tri bien (khong dem 0)
        sm = np.convolve(np.pad(v, (k // 2, k - 1 - k // 2), mode="edge"), np.ones(k) / k, mode="valid")
    else:
        sm = v
    return v, sm


class LossPlot(QtWidgets.QWidget):
    """Duong loss (net mo = tung buoc, net dam = trung binh truot)."""

    def __init__(self):
        super().__init__()
        self.vals = []
        self.setMinimumHeight(150)

    def clear(self):
        self.vals = []
        self.update()

    def add(self, v):
        self.vals.append(v)
        self.update()

    def paintEvent(self, _):
        p = QtGui.QPainter(self)
        p.setRenderHint(QtGui.QPainter.Antialiasing)
        r = self.rect()
        p.fillRect(r, QtGui.QColor("#fafafa"))
        p.setPen(QtGui.QColor("#cccccc"))
        p.drawRect(r.adjusted(0, 0, -1, -1))
        if len(self.vals) < 2:
            p.setPen(QtGui.QColor("#999999"))
            p.drawText(r, Qt.AlignCenter, "Đường loss sẽ hiện khi huấn luyện")
            return
        v, sm = smooth_curve(self.vals)
        lo, hi = float(v.min()), float(v.max())
        pad = 28
        W, H = r.width() - pad - 8, r.height() - 20
        xs = pad + np.arange(len(v)) / (len(v) - 1) * W
        ys = lambda a: 8 + H - (a - lo) / max(hi - lo, 1e-12) * H

        def poly(arr, color, width):
            p.setPen(QtGui.QPen(QtGui.QColor(color), width))
            pts = [QtCore.QPointF(x, ys(a)) for x, a in zip(xs, arr)]
            p.drawPolyline(QtGui.QPolygonF(pts))

        poly(v, "#9ec5fe", 1)
        poly(sm, "#0d6efd", 2)
        p.setPen(QtGui.QColor("#555555"))
        p.drawText(2, 16, f"{hi:.4f}")
        p.drawText(2, r.height() - 4, f"{lo:.4f}")
        p.drawText(r.width() - 90, r.height() - 4, f"bước {len(self.vals)}")


def readonly_table(headers):
    t = QtWidgets.QTableWidget(0, len(headers))
    t.setHorizontalHeaderLabels(headers)
    t.setEditTriggers(QtWidgets.QAbstractItemView.NoEditTriggers)
    t.setSelectionBehavior(QtWidgets.QAbstractItemView.SelectRows)
    t.verticalHeader().setVisible(False)
    t.horizontalHeader().setSectionResizeMode(QtWidgets.QHeaderView.Stretch)
    return t


def add_row(table, cells, bad=False):
    r = table.rowCount()
    table.insertRow(r)
    for c, text in enumerate(cells):
        it = QtWidgets.QTableWidgetItem(str(text))
        if bad:
            it.setBackground(QtGui.QColor("#ffe3e3"))
        table.setItem(r, c, it)


# --------------------------------------------------------------------------- tab 1: du lieu
class DataTab(QtWidgets.QWidget):
    def __init__(self, settings):
        super().__init__()
        self.settings = settings
        self.worker = None
        self.stale = False
        self.images = QtWidgets.QLineEdit(existing(settings.value("images", ""), ROOT, want_dir=True))
        default_labels = HERE / "output_v1"
        self.labels = QtWidgets.QLineEdit(existing(settings.value("labels", ""), default_labels if default_labels.is_dir() else HERE / "labels", want_dir=True))
        form = QtWidgets.QFormLayout()
        form.addRow("Thư mục ảnh:", path_row(self.images, "dir", self))
        form.addRow("Thư mục nhãn (*_points.csv):", path_row(self.labels, "dir", self))
        self.btn = QtWidgets.QPushButton("Kiểm tra dữ liệu")
        self.btn.clicked.connect(self.run_check)
        self.status = QtWidgets.QLabel("Chọn thư mục rồi bấm Kiểm tra.")
        self.status.setWordWrap(True)
        self.table = readonly_table(["Ảnh", "Kích thước", "Số nhãn", "Khoảng cách NN (giữa)", "NN nhỏ nhất",
                                     "Ngoài bó dây", "Sáng TB", "Cảnh báo"])
        hint = QtWidgets.QLabel(
            "Gợi ý: khoảng cách NN giữa ≈ đường kính sợi (≈ %d px). Nếu lệch nhiều → ảnh khác độ phóng đại, "
            "hãy đổi tỉ lệ ảnh trước. Cùng một dây thì số nhãn mỗi ảnh nên bằng nhau." % fc.DIAMETER)
        hint.setWordWrap(True)
        lay = QtWidgets.QVBoxLayout(self)
        lay.addLayout(form)
        lay.addWidget(self.btn)
        lay.addWidget(self.status)
        lay.addWidget(self.table, 1)
        lay.addWidget(hint)

    def paths(self):
        return self.images.text().strip(), self.labels.text().strip()

    def mark_stale(self):
        """Nhan vua duoc luu o tab Dan nhan: ket qua kiem tra cu khong con dung."""
        self.stale = True
        self.status.setText("Nhãn vừa thay đổi — sẽ kiểm tra lại khi bạn quay về tab này.")

    def run_check(self):
        img, lab = self.paths()
        self.stale = False
        self.settings.setValue("images", img)
        self.settings.setValue("labels", lab)
        self.table.setRowCount(0)
        self.btn.setEnabled(False)
        self.status.setText("Đang kiểm tra…")

        def job(w):
            pairs = tf.find_pairs(img, lab, log=lambda s: None)
            ok, rows = tf.check(pairs, log=lambda s: None)
            return len(pairs), ok, rows

        self.worker = Worker(job, self, quiet=True)
        self.worker.done.connect(self._done)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _done(self, res):
        n, ok, rows = res
        self.btn.setEnabled(True)
        for r in rows:
            add_row(self.table, [r["name"], r["size"], r["n"], f'{r["nn_med"]:.1f}', f'{r["nn_min"]:.1f}',
                                 r["out"], f'{r["mean"]:.1f}', "; ".join(r["flags"])], bad=bool(r["flags"]))
        if n == 0:
            self.status.setText("❌ Không tìm thấy cặp ảnh/nhãn nào. Tên nhãn phải là <tên_ảnh>_points.csv.")
        elif ok:
            counts = [r["n"] for r in rows]
            self.status.setText(f"✅ {n} cặp ảnh/nhãn, dữ liệu OK (số nhãn {min(counts)}–{max(counts)}).")
        else:
            self.status.setText(f"⚠️ {n} cặp ảnh/nhãn, có cảnh báo (dòng tô đỏ). Vẫn huấn luyện được nhưng nên sửa trước.")

    def _failed(self, tb):
        self.btn.setEnabled(True)
        self.status.setText("❌ Lỗi:\n" + tb.splitlines()[-1])


# --------------------------------------------------------------------------- tab: dan nhan
# Mau diem theo NGUON: de biet diem nao may tu danh dau (chua xac nhan), diem nao ban them, diem nao nap tu CSV.
ORIGIN_COLORS = {"auto": "#ffa726", "user": "#2ecc71", "saved": "#4aa3ff"}
ORIGIN_NAMES = {"auto": "máy đánh dấu (chưa xác nhận)", "user": "bạn thêm", "saved": "từ CSV đã lưu"}
MARKER_SHAPES = {"circle": "Tròn", "square": "Vuông", "diamond": "Thoi", "cross": "Chữ thập", "dot": "Chỉ chấm tâm"}
CLOSE_RATIO = 0.55                               # hai tam gan hon 0.55 D (~34 px) bi canh bao (thuc te nho nhat ~0.6 D)


class LabelCanvas(QtWidgets.QWidget):
    """Vung ve anh de dan nhan. Chuot trai: them | chuot phai: xoa | cuon: zoom | Ctrl+keo hoac chuot giua+keo: di chuyen."""
    changed = pyqtSignal()                       # chi phat khi NGUOI DUNG sua
    rejected = pyqtSignal(str)                   # thao tac bi tu choi (de hien thong bao)

    def __init__(self):
        super().__init__()
        self.setMinimumSize(520, 380)
        self.setMouseTracking(True)
        self.setFocusPolicy(Qt.StrongFocus)
        self.base, self.origin = None, (0, 0)
        self.lab = lp.Labeler([], fc.DIAMETER * 0.7)
        self.scale, self.fit, self.off, self._fitted = 1.0, 1.0, QtCore.QPointF(0, 0), True
        self._press = self._last = self._hover = None
        self._pan, self._moved = False, 0.0
        self.origin_of, self.auto_total, self.show_numbers = {}, 0, False
        self._flags, self._flags_dirty = [], True
        # Kieu hien thi diem (chi la hinh ve, KHONG doi toa do tam duoc luu)
        self.marker_shape, self.marker_size, self.marker_width = "circle", float(fc.DIAMETER), 1.8

    # ---- du lieu
    def set_image(self, gray, bbox, pts):
        y0, y1, x0, x1 = bbox
        crop = np.ascontiguousarray(fc.enhance(gray)[y0:y1, x0:x1])
        h, w = crop.shape
        self.base = QtGui.QPixmap.fromImage(QtGui.QImage(crop.data, w, h, w, QtGui.QImage.Format_Grayscale8).copy())
        self.origin = (x0, y0)
        self.lab = lp.Labeler(pts, fc.DIAMETER * 0.7)
        self.origin_of = {tuple(map(int, q)): "saved" for q in pts}     # diem nap tu CSV
        self.auto_total, self._flags_dirty = 0, True
        self.fit_view()

    def set_points(self, pts, user_edit=False):
        """Diem do MAY danh dau (xem ORIGIN_COLORS['auto'])."""
        self.lab = lp.Labeler(pts, fc.DIAMETER * 0.7)
        self.origin_of = {tuple(map(int, q)): "auto" for q in pts}
        self.auto_total, self._flags_dirty = len(pts), True
        self.update()
        if user_edit:
            self.changed.emit()

    def replace_points(self, pts):
        """Thay toan bo diem nhung van hoan tac duoc (vd "Danh dau lai")."""
        self.lab.history.append(list(self.lab.pts))
        self.lab.pts = [tuple(map(int, p)) for p in pts]
        for q in self.lab.pts:
            self.origin_of[q] = "auto"
        self.auto_total, self._flags_dirty = len(self.lab.pts), True
        self.changed.emit()
        self.update()

    def set_marker(self, shape=None, size=None, width=None):
        """Doi hinh dang / co (duong kinh hoac canh, pixel anh goc) / do day net cua dau danh dau."""
        if shape in MARKER_SHAPES:
            self.marker_shape = shape
        if size is not None:
            self.marker_size = float(min(max(size, 6), 400))
        if width is not None:
            self.marker_width = float(min(max(width, 0.5), 8))
        self.update()

    def _shape(self, p, c, r, ring=False):
        """Ve 1 dau danh dau tam c, ban kinh/nua canh r (pixel man hinh). ring=True: luon ve khung (canh bao)."""
        sh = self.marker_shape
        if sh == "circle" or (sh == "dot" and ring):
            p.drawEllipse(c, r, r)
        elif sh == "square":
            p.drawRect(QtCore.QRectF(c.x() - r, c.y() - r, 2 * r, 2 * r))
        elif sh == "diamond":
            p.drawPolygon(QtGui.QPolygonF([QtCore.QPointF(c.x(), c.y() - r), QtCore.QPointF(c.x() + r, c.y()),
                                           QtCore.QPointF(c.x(), c.y() + r), QtCore.QPointF(c.x() - r, c.y())]))
        elif sh == "cross":
            p.drawLine(QtCore.QPointF(c.x() - r, c.y()), QtCore.QPointF(c.x() + r, c.y()))
            p.drawLine(QtCore.QPointF(c.x(), c.y() - r), QtCore.QPointF(c.x(), c.y() + r))

    def origin_stats(self):
        """May danh dau bao nhieu, giu lai bao nhieu, xoa bao nhieu, ban them bao nhieu, bao nhieu cap qua gan."""
        kinds = [self.origin_of.get(q, "saved") for q in self.lab.pts]
        kept = kinds.count("auto")
        return dict(auto=self.auto_total, kept=kept, removed=max(self.auto_total - kept, 0),
                    user=kinds.count("user"), saved=kinds.count("saved"), close=len(self.close_pairs()))

    def close_pairs(self):
        """Cac cap (i, j) chi so diem gan nhau bat thuong. Co cache, tinh lai khi diem thay doi."""
        if self._flags_dirty:
            pts = np.array(self.lab.pts, float).reshape(-1, 2)
            self._flags = []
            if len(pts) > 1:
                d = np.hypot(pts[:, None, 0] - pts[None, :, 0], pts[:, None, 1] - pts[None, :, 1])
                i, j = np.nonzero(np.triu(d < fc.DIAMETER * CLOSE_RATIO, 1))
                self._flags = list(zip(i.tolist(), j.tolist()))
            self._flags_dirty = False
        return self._flags

    def points(self):
        return list(self.lab.pts)

    def clear_image(self):
        self.base = None
        self.lab = lp.Labeler([], fc.DIAMETER * 0.7)
        self.origin_of, self.auto_total, self._flags_dirty = {}, 0, True
        self.update()

    # ---- hinh hoc
    def fit_view(self):
        if self.base is None:
            return
        w, h = self.base.width(), self.base.height()
        self.fit = min(self.width() / w, self.height() / h) * 0.98
        self.scale = self.fit
        self.off = QtCore.QPointF((self.width() - w * self.scale) / 2, (self.height() - h * self.scale) / 2)
        self._fitted = True
        self.update()

    def to_img(self, p):
        return ((p.x() - self.off.x()) / self.scale + self.origin[0],
                (p.y() - self.off.y()) / self.scale + self.origin[1])

    def to_scr(self, x, y):
        return QtCore.QPointF(self.off.x() + (x - self.origin[0]) * self.scale,
                              self.off.y() + (y - self.origin[1]) * self.scale)

    def inside(self, x, y):
        return (self.base is not None and 0 <= x - self.origin[0] < self.base.width()
                and 0 <= y - self.origin[1] < self.base.height())

    def zoom(self, factor, anchor=None):
        if self.base is None:
            return
        anchor = anchor or QtCore.QPointF(self.width() / 2, self.height() / 2)
        new = min(max(self.scale * factor, self.fit * 0.5), 8.0)
        k = new / self.scale
        self.off = QtCore.QPointF(anchor.x() - (anchor.x() - self.off.x()) * k,
                                  anchor.y() - (anchor.y() - self.off.y()) * k)
        self.scale, self._fitted = new, False
        self.update()

    # ---- sua nhan (goi tu chuot, cung dung duoc tu ma)
    def add_point(self, x, y):
        if not self.inside(x, y):
            return
        if self.lab.pts:
            d = min(((px - x) ** 2 + (py - y) ** 2) ** 0.5 for px, py in self.lab.pts)
            if d < fc.DIAMETER * 0.45:          # hai tam soi that khong bao gio gan the (min thuc te ~0.6 D)
                self.rejected.emit(f"Bỏ qua: đã có điểm cách {d:.0f}px (hai tâm sợi thường cách ≥ "
                                   f"{fc.DIAMETER * 0.6:.0f}px). Bấm chuột phải để xóa điểm sai.")
                return
        self.lab.add(x, y)
        self.origin_of[(int(x), int(y))] = "user"
        self._flags_dirty = True
        self.changed.emit()
        self.update()

    def remove_near(self, x, y):
        if self.lab.remove_near(x, y):
            self._flags_dirty = True
            self.changed.emit()
            self.update()

    def undo(self):
        if self.lab.history:
            self.lab.undo()
            self._flags_dirty = True
            self.changed.emit()
            self.update()

    def clear_points(self):
        if self.lab.pts:
            self.lab.history.append(list(self.lab.pts))
            self.lab.pts = []
            self._flags_dirty = True
            self.changed.emit()
            self.update()

    # ---- su kien
    def resizeEvent(self, e):
        super().resizeEvent(e)
        if self._fitted:
            self.fit_view()

    def wheelEvent(self, e):
        self.zoom(1.15 ** (e.angleDelta().y() / 120), QtCore.QPointF(e.pos()))

    def mousePressEvent(self, e):
        self.setFocus()
        self._press, self._last, self._moved = e.pos(), e.pos(), 0.0
        self._pan = e.button() == Qt.MiddleButton or (e.button() == Qt.LeftButton and bool(e.modifiers() & Qt.ControlModifier))

    def mouseMoveEvent(self, e):
        self._hover = e.pos()
        if self._press is not None:
            d = e.pos() - self._last
            self._moved += abs(d.x()) + abs(d.y())
            if self._pan:
                self.off += QtCore.QPointF(d)
                self._fitted = False
            self._last = e.pos()
        self.update()

    def mouseReleaseEvent(self, e):
        if self._press is not None and not self._pan and self._moved < 6:
            x, y = self.to_img(e.pos())
            if e.button() == Qt.LeftButton:
                self.add_point(x, y)
            elif e.button() == Qt.RightButton:
                self.remove_near(x, y)
        self._press, self._pan = None, False

    def leaveEvent(self, _):
        self._hover = None
        self.update()

    def _draw_legend(self, p):
        st = self.origin_stats()
        rows = [(ORIGIN_COLORS[k], f"{ORIGIN_NAMES[k]}: {st[k if k != 'auto' else 'kept']}")
                for k in ("auto", "user", "saved") if st["kept" if k == "auto" else k]]
        if st["close"]:
            rows.append(("#ff1744", f"{st['close']} cặp điểm quá gần"))
        if not rows:
            return
        p.setFont(QtGui.QFont(p.font().family(), 9))
        w = 28 + max(p.fontMetrics().horizontalAdvance(t) for _, t in rows)
        p.setPen(Qt.NoPen)
        p.setBrush(QtGui.QColor(0, 0, 0, 150))
        p.drawRoundedRect(QtCore.QRectF(8, 8, w, 8 + 18 * len(rows)), 5, 5)
        for k, (color, text) in enumerate(rows):
            y = 8 + 14 + 18 * k
            p.setBrush(QtGui.QColor(color))
            p.drawEllipse(QtCore.QPointF(20, y - 4), 5, 5)
            p.setPen(QtGui.QColor("#f0f0f0"))
            p.drawText(QtCore.QPointF(32, y), text)
            p.setPen(Qt.NoPen)

    def paintEvent(self, _):
        p = QtGui.QPainter(self)
        p.setRenderHint(QtGui.QPainter.Antialiasing)
        p.setRenderHint(QtGui.QPainter.SmoothPixmapTransform)
        p.fillRect(self.rect(), QtGui.QColor("#202020"))
        if self.base is None:
            p.setPen(QtGui.QColor("#aaaaaa"))
            p.drawText(self.rect(), Qt.AlignCenter, "Chọn một ảnh ở danh sách bên trái")
            return
        p.save()
        p.translate(self.off)
        p.scale(self.scale, self.scale)
        p.drawPixmap(0, 0, self.base)
        p.restore()
        r = max(self.marker_size / 2 * self.scale, 4)
        pens = {k: (QtGui.QPen(QtGui.QColor(c), self.marker_width), QtGui.QPen(QtGui.QColor(c), 5, Qt.SolidLine, Qt.RoundCap))
                for k, c in ORIGIN_COLORS.items()}
        font = p.font()
        font.setPointSizeF(max(7.0, min(11.0, 8 * self.scale + 5)))
        p.setFont(font)
        for i, (x, y) in enumerate(self.lab.pts, 1):
            ring, dot = pens[self.origin_of.get((x, y), "saved")]
            c = self.to_scr(x, y)
            p.setPen(ring)
            p.setBrush(Qt.NoBrush)
            self._shape(p, c, r)
            p.setPen(dot)
            p.drawPoint(c)
            if self.show_numbers:
                p.setPen(QtGui.QColor("#ffffff"))
                p.drawText(c + QtCore.QPointF(-8, -6), str(i))
        p.setPen(QtGui.QPen(QtGui.QColor("#ff1744"), 2.6))                 # canh bao: cap diem qua gan nhau
        for i, j in self.close_pairs():
            ca, cb = self.to_scr(*self.lab.pts[i]), self.to_scr(*self.lab.pts[j])
            self._shape(p, ca, r + 3, ring=True)
            self._shape(p, cb, r + 3, ring=True)
            p.drawLine(ca, cb)
        self._draw_legend(p)
        if self._hover is not None and not self._pan:
            p.setPen(QtGui.QPen(QtGui.QColor(255, 255, 0, 160), 1, Qt.DashLine))
            p.setBrush(Qt.NoBrush)
            self._shape(p, QtCore.QPointF(self._hover), r, ring=True)


class LabelTab(QtWidgets.QWidget):
    saved = pyqtSignal()

    def __init__(self, settings, get_paths):
        super().__init__()
        self.settings, self.get_paths = settings, get_paths
        self.files, self.cur, self.dirty, self._gray = [], -1, False, None
        self.expected_by, self._token, self._workers = {}, 0, []
        self.labeled = set()                         # ten (stem) cac anh da co CSV nhan
        self._net = self._net_key = self._cp = None

        self.list = QtWidgets.QListWidget()
        self.list.setMinimumWidth(230)
        self.list.setMaximumWidth(300)
        self.list.currentRowChanged.connect(self.open_row)
        self.canvas = LabelCanvas()
        self.canvas.changed.connect(self._edited)
        self.canvas.rejected.connect(lambda m: self.status.setText(m))

        self.load_btn = QtWidgets.QPushButton("Nạp thư mục (từ tab Dữ liệu)")
        self.load_btn.clicked.connect(self.load_folder)
        self.source = QtWidgets.QComboBox()
        self.source.addItems(["Đánh dấu sẵn: FastNet (nhanh)", "Đánh dấu sẵn: Cellpose (chậm, cho dây khác)",
                              "Không đánh dấu sẵn"])
        self.source.setCurrentIndex(min(sget(settings, "label/source", 0, int), 2))
        self.weights = QtWidgets.QLineEdit(existing(sget(settings, "label/weights", ""), MODELS / "fastnet.pt"))
        self.weights.setToolTip("Trọng số FastNet (.pt hoặc .xml) dùng để đánh dấu sẵn")
        top = QtWidgets.QHBoxLayout()
        top.addWidget(self.load_btn)
        top.addWidget(self.source)
        top.addWidget(self.weights, 1)

        self.count = QtWidgets.QLabel("")
        self.expected = QtWidgets.QSpinBox()
        self.expected.setRange(0, 5000)
        self.expected.setSpecialValueText("không")
        self.expected.valueChanged.connect(self._expected_changed)
        self.status = QtWidgets.QLabel("")
        self.status.setWordWrap(True)
        self.origin_lbl = QtWidgets.QLabel("")
        self.origin_lbl.setWordWrap(True)
        self.num_chk = QtWidgets.QCheckBox("Đánh số")
        self.num_chk.toggled.connect(self._toggle_numbers)
        # kieu dau danh dau: hinh dang, co, do day net (luu cai dat)
        self.shape_box = QtWidgets.QComboBox()
        for key, name in MARKER_SHAPES.items():
            self.shape_box.addItem(name, key)
        self.shape_box.setCurrentIndex(max(self.shape_box.findData(sget(settings, "label/shape", "circle")), 0))
        self.size_slider = QtWidgets.QSlider(Qt.Horizontal)
        self.size_slider.setRange(6, 150)
        self.size_slider.setMinimumWidth(120)
        self.size_spin = QtWidgets.QSpinBox()
        self.size_spin.setRange(6, 150)
        self.size_spin.setSuffix(" px")
        self.size_spin.setValue(sget(settings, "label/size", int(fc.DIAMETER), int))
        self.size_slider.setValue(self.size_spin.value())
        self.width_spin = QtWidgets.QDoubleSpinBox()
        self.width_spin.setRange(0.5, 8.0)
        self.width_spin.setSingleStep(0.5)
        self.width_spin.setValue(sget(settings, "label/width", 1.8, float))
        self.size_slider.valueChanged.connect(self.size_spin.setValue)
        self.size_spin.valueChanged.connect(lambda v: (self.size_slider.setValue(v), self._marker_changed()))
        self.shape_box.currentIndexChanged.connect(lambda _: self._marker_changed())
        self.width_spin.valueChanged.connect(lambda _: self._marker_changed())
        self.size_spin.setToolTip("Kích thước hiển thị của dấu (đường kính/cạnh, pixel ảnh gốc). Không ảnh hưởng toạ độ tâm được lưu.")
        self.filter = QtWidgets.QComboBox()
        self.filter.addItems(["Hiện: tất cả ảnh", "Hiện: chưa dán nhãn", "Hiện: đã có nhãn"])
        self.filter.currentIndexChanged.connect(lambda _: self._apply_filter())
        self.progress_lbl = QtWidgets.QLabel("")
        bar = QtWidgets.QHBoxLayout()
        for text, fn, tip in (
                ("↶ Hoàn tác", self.canvas.undo, "Ctrl+Z"),
                ("Xóa hết", self.canvas.clear_points, ""),
                ("Đánh dấu lại", lambda: self.prelabel(user=True), "Chạy lại đánh dấu tự động (ghi đè điểm hiện có)"),
                ("Vừa khung", self.canvas.fit_view, ""),
                ("◀", lambda: self.step(-1), "Ảnh trước (Ctrl+←)"),
                ("▶", lambda: self.step(1), "Ảnh sau (Ctrl+→)"),
                ("💾 Lưu", self.save, "Ctrl+S"),
                ("💾 Lưu && ảnh tiếp", lambda: self.save(go_next=True), "")):
            bar.addWidget(self._btn(text, fn, tip))
        bar.addStretch(1)
        info = QtWidgets.QHBoxLayout()
        info.addWidget(self.count)
        info.addSpacing(16)
        info.addWidget(QtWidgets.QLabel("Số đúng (tuỳ chọn):"))
        info.addWidget(self.expected)
        info.addSpacing(16)
        info.addWidget(self.num_chk)
        info.addStretch(1)
        help_ = QtWidgets.QLabel("Chuột trái: thêm (bấm vào TÂM sợi) • Chuột phải: xóa • Cuộn: zoom • "
                                 "Ctrl+kéo hoặc chuột giữa+kéo: di chuyển • Ctrl+S lưu • Ctrl+Z hoàn tác<br>"
                                 "Màu vòng: <span style='color:#e08a00'>cam = máy đánh dấu (chưa xác nhận)</span> • "
                                 "<span style='color:#1e9e55'>xanh lá = bạn thêm</span> • "
                                 "<span style='color:#2f7de1'>xanh dương = từ CSV</span> • "
                                 "<span style='color:#d50032'>đỏ = hai điểm quá gần nhau</span>")
        help_.setStyleSheet("color:#666;")
        help_.setWordWrap(True)

        style = QtWidgets.QHBoxLayout()
        style.addWidget(QtWidgets.QLabel("Dấu:"))
        style.addWidget(self.shape_box)
        style.addWidget(QtWidgets.QLabel("Cỡ:"))
        style.addWidget(self.size_slider)
        style.addWidget(self.size_spin)
        style.addWidget(QtWidgets.QLabel("Nét:"))
        style.addWidget(self.width_spin)
        reset = self._btn("Mặc định", self._marker_reset, "Về hình tròn, cỡ bằng đường kính sợi")
        style.addWidget(reset)
        style.addStretch(1)
        right = QtWidgets.QVBoxLayout()
        right.addLayout(info)
        right.addLayout(style)
        right.addWidget(self.origin_lbl)
        right.addWidget(self.canvas, 1)
        right.addLayout(bar)
        right.addWidget(self.status)
        right.addWidget(help_)
        body = QtWidgets.QHBoxLayout()
        left_col = QtWidgets.QVBoxLayout()
        left_col.addWidget(self.filter)
        left_col.addWidget(self.progress_lbl)
        left_col.addWidget(self.list, 1)
        body.addLayout(left_col)
        body.addLayout(right, 1)
        lay = QtWidgets.QVBoxLayout(self)
        lay.addLayout(top)
        lay.addLayout(body, 1)

        for key, fn in (("Ctrl+S", self.save), ("Ctrl+Z", self.canvas.undo),
                        ("Ctrl+Right", lambda: self.step(1)), ("Ctrl+Left", lambda: self.step(-1))):
            QtWidgets.QShortcut(QtGui.QKeySequence(key), self, activated=fn)
        self._marker_changed()
        self._update_count()

    def _marker_changed(self):
        self.canvas.set_marker(self.shape_box.currentData(), self.size_spin.value(), self.width_spin.value())

    def _marker_reset(self):
        self.shape_box.setCurrentIndex(self.shape_box.findData("circle"))
        self.size_spin.setValue(int(fc.DIAMETER))
        self.width_spin.setValue(1.8)

    def save_settings(self):
        self.settings.setValue("label/shape", self.shape_box.currentData())
        self.settings.setValue("label/size", self.size_spin.value())
        self.settings.setValue("label/width", self.width_spin.value())
        self.settings.setValue("label/source", self.source.currentIndex())
        self.settings.setValue("label/weights", self.weights.text())

    @staticmethod
    def _btn(text, fn, tip=""):
        b = QtWidgets.QPushButton(text)
        b.setToolTip(tip)
        b.clicked.connect(lambda _=False: fn())
        return b

    # ---- danh sach anh
    def _csv(self, f):
        return Path(self.get_paths()[1]) / f"{f.stem}_points.csv"

    def _item_text(self, f):
        c = self._csv(f)
        if c.is_file():
            try:
                return f"✓ {f.stem}  ({len(lp.read_points(c))})"
            except Exception:
                return f"⚠ {f.stem}  (CSV lỗi)"
        return f"○ {f.stem}"

    def _refresh_marks(self):
        """Tinh lai tap anh da co nhan, tien do k/N va bo loc danh sach."""
        self.labeled = {f.stem for f in self.files if self._csv(f).is_file()}
        self.progress_lbl.setText(f"Đã dán nhãn: {len(self.labeled)}/{len(self.files)} ảnh")
        self._apply_filter()

    def _apply_filter(self):
        mode = self.filter.currentIndex()
        for i, f in enumerate(self.files):
            done = f.stem in self.labeled
            self.list.item(i).setHidden((mode == 1 and done) or (mode == 2 and not done))

    def _toggle_numbers(self, on):
        self.canvas.show_numbers = on
        self.canvas.update()

    def load_folder(self):
        if not self.maybe_save():
            return
        images, labels = self.get_paths()
        self.files = fc.collect_images([images])
        self.cur, self.dirty = -1, False
        self.list.blockSignals(True)
        self.list.clear()
        for f in self.files:
            self.list.addItem(self._item_text(f))
        self.list.blockSignals(False)
        self._refresh_marks()
        self.canvas.clear_image()
        self._update_count()
        if not self.files:
            self.status.setText("❌ Không thấy ảnh nào trong thư mục ảnh.")
            return
        self.status.setText(f"{len(self.files)} ảnh • nhãn lưu vào: {labels}  (✓ = đã có nhãn, ○ = chưa)")
        first = next((i for i in range(len(self.files)) if not self.list.item(i).isHidden()), 0)
        self.list.setCurrentRow(first)

    # ---- mo anh
    def open_row(self, row):
        if row < 0 or row == self.cur or row >= len(self.files):
            return
        if not self.maybe_save():
            self.list.blockSignals(True)
            self.list.setCurrentRow(self.cur)
            self.list.blockSignals(False)
            return
        f = self.files[row]
        gray = cv2.imread(str(f), cv2.IMREAD_GRAYSCALE)
        if gray is None:
            self.status.setText(f"❌ Không đọc được {f.name}")
            self.list.blockSignals(True)                  # tra danh sach ve anh dang hien de khong bi lech
            self.list.setCurrentRow(self.cur)
            self.list.blockSignals(False)
            return
        self.cur, self.dirty, self._gray = row, False, gray
        bbox = fc.find_bundle_bbox(gray, margin=int(fc.DIAMETER * 1.1))
        csv_path = self._csv(f)
        pts = lp.read_points(csv_path) if csv_path.is_file() else []
        self.canvas.set_image(gray, bbox, [(int(x), int(y)) for x, y in pts])
        self.expected.blockSignals(True)
        self.expected.setValue(self.expected_by.get(f.stem, 0))
        self.expected.blockSignals(False)
        self._update_count()
        if csv_path.is_file():
            self.status.setText(f"{f.name}: nạp {len(pts)} điểm từ CSV đã lưu.")
        else:
            self.status.setText(f"{f.name}: chưa có nhãn.")
            if self.source.currentIndex() != 2:
                self.prelabel()

    def step(self, d):
        r = self.cur + d
        while 0 <= r < len(self.files) and self.list.item(r).isHidden():     # bo qua anh dang bi loc an
            r += d
        if 0 <= r < len(self.files):
            self.list.setCurrentRow(r)

    # ---- danh dau san
    def prelabel(self, user=False):
        if self.cur < 0:
            return
        if user and self.canvas.lab.pts and QtWidgets.QMessageBox.question(
                self, "Đánh dấu lại", "Ghi đè các điểm hiện có bằng đánh dấu tự động?") != QtWidgets.QMessageBox.Yes:
            return
        self._token += 1
        token, gray, row = self._token, self._gray, self.cur
        use_cp = self.source.currentIndex() == 1
        wpath = self.weights.text().strip()
        self.status.setText("Đang đánh dấu tự động…" + (" (Cellpose, có thể mất ~1 phút)" if use_cp else ""))

        def job(w):
            if use_cp:
                if FROZEN:
                    raise RuntimeError("Bản đóng gói không kèm Cellpose (mô hình 1,2 GB). "
                                       "Dùng bản chạy bằng Python để đánh dấu sẵn bằng Cellpose.")
                import cellpose_counter as cpc
                if self._cp is None:
                    self._cp = cpc.load_model(device="auto")
                return cpc.segment(gray, self._cp, fc.DIAMETER)[0]
            if self._net is None or self._net_key != wpath:
                self._net, self._net_key = fc.load_net(wpath), wpath
            return fc.count(gray, self._net)

        w = Worker(job, self, quiet=True)
        w.done.connect(lambda pts: self._prelabel_done(token, row, pts, user))
        w.failed.connect(lambda tb: self.status.setText("❌ Đánh dấu tự động lỗi: " + tb.strip().splitlines()[-1]))
        w.finished.connect(lambda: self._workers.remove(w) if w in self._workers else None)
        self._workers.append(w)
        w.start()

    def _prelabel_done(self, token, row, pts, user):
        if token != self._token or row != self.cur:
            return                                   # da chuyen anh khac
        if not user and self.canvas.lab.history:
            return                                   # nguoi dung da sua tay trong luc cho
        pts = [(int(x), int(y)) for x, y in pts]
        if user:
            self.canvas.replace_points(pts)              # co the Ctrl+Z de lay lai diem cu
        else:
            self.canvas.set_points(pts)
        self._update_count()
        self.status.setText(f"{self.files[self.cur].name}: đánh dấu sẵn {len(pts)} điểm — "
                            "hãy kiểm tra, thêm/xóa rồi Lưu.")

    # ---- sua / luu
    def _edited(self):
        self.dirty = True
        self._update_count()

    def _expected_changed(self, v):
        if self.cur >= 0:
            self.expected_by[self.files[self.cur].stem] = v
        self._update_count()

    def _update_origin(self):
        st = self.canvas.origin_stats()
        parts = []
        if st["auto"]:
            parts.append(f"máy đánh dấu {st['auto']} → bạn giữ {st['kept']}, xóa {st['removed']}")
        if st["user"]:
            parts.append(f"bạn thêm {st['user']}")
        if st["saved"]:
            parts.append(f"từ CSV {st['saved']}")
        if st["close"]:
            parts.append(f"⚠ {st['close']} cặp điểm quá gần nhau (vòng đỏ) — kiểm tra có bấm trùng không")
        self.origin_lbl.setText(" • ".join(parts))
        self.origin_lbl.setStyleSheet("color:#b00020;" if st["close"] else "color:#555;")

    def _update_count(self):
        self._update_origin()
        if self.cur < 0:
            self.count.setText("")
            return
        n, exp = len(self.canvas.lab.pts), self.expected.value()
        txt = f"Số sợi: {n}" + ("  •  chưa lưu" if self.dirty else "")
        color = "#222222"
        if exp:
            txt += f"  (đúng {exp}, lệch {n - exp:+d})"
            color = "#1e8e3e" if n == exp else "#d93025"
        self.count.setText(txt)
        self.count.setStyleSheet(f"font-size:18px; font-weight:bold; color:{color};")

    def save(self, go_next=False):
        if self.cur < 0:
            return True
        f, pts = self.files[self.cur], self.canvas.points()
        out = self._csv(f)
        out.parent.mkdir(parents=True, exist_ok=True)
        lp.write_points(out, pts)
        self.dirty = False
        self.list.item(self.cur).setText(self._item_text(f))
        self._refresh_marks()
        self._update_count()
        self.status.setText(f"💾 Đã lưu {len(pts)} điểm → {out}")
        self.saved.emit()
        if go_next:
            self.step(1)
        return True

    def maybe_save(self):
        """Hỏi nếu còn thay đổi chưa lưu. True = được phép đi tiếp."""
        if not self.dirty or self.cur < 0:
            return True
        ans = QtWidgets.QMessageBox.question(
            self, "Chưa lưu", f"{self.files[self.cur].stem} có thay đổi chưa lưu.",
            QtWidgets.QMessageBox.Save | QtWidgets.QMessageBox.Discard | QtWidgets.QMessageBox.Cancel)
        if ans == QtWidgets.QMessageBox.Save:
            return self.save()
        return ans == QtWidgets.QMessageBox.Discard


# --------------------------------------------------------------------------- tab 3: huan luyen
class ValDialog(QtWidgets.QDialog):
    """Chon cac anh dung de kiem tra (khong dung de hoc)."""

    def __init__(self, parent, names, checked):
        super().__init__(parent)
        self.setWindowTitle("Chọn ảnh kiểm tra")
        self.resize(420, 480)
        info = QtWidgets.QLabel("Các ảnh được tick sẽ KHÔNG dùng để học, chỉ để đo độ chính xác. "
                                "Nên chọn ảnh khác góc xoay/độ sáng (hoặc khác loại dây) so với phần còn lại.")
        info.setWordWrap(True)
        self.list = QtWidgets.QListWidget()
        for n in names:
            it = QtWidgets.QListWidgetItem(n)
            it.setFlags(it.flags() | Qt.ItemIsUserCheckable)
            it.setCheckState(Qt.Checked if n in checked else Qt.Unchecked)
            self.list.addItem(it)
        box = QtWidgets.QDialogButtonBox(QtWidgets.QDialogButtonBox.Ok | QtWidgets.QDialogButtonBox.Cancel)
        box.accepted.connect(self.accept)
        box.rejected.connect(self.reject)
        lay = QtWidgets.QVBoxLayout(self)
        lay.addWidget(info)
        lay.addWidget(self.list, 1)
        lay.addWidget(box)

    def selected(self):
        return [self.list.item(i).text() for i in range(self.list.count())
                if self.list.item(i).checkState() == Qt.Checked]


def backup_file(dst):
    """Sao luu dst canh no (fastnet_backup_<gio>.pt). Tra ve duong dan ban sao, hoac None."""
    dst = Path(dst)
    if not dst.is_file():
        return None
    bak = dst.with_name(f"{dst.stem}_backup_{time.strftime('%Y%m%d_%H%M%S')}{dst.suffix}")
    shutil.copy2(dst, bak)
    return bak


class TrainTab(QtWidgets.QWidget):
    trained = pyqtSignal(str, float)             # (file trong so, nguong goi y) -> tab Thu nhanh dung ngay

    def __init__(self, settings, get_paths):
        super().__init__()
        self.settings, self.get_paths, self.worker, self.last_out = settings, get_paths, None, None
        S = lambda k, d, c=str: sget(settings, "train/" + k, d, c)
        self.iters = QtWidgets.QSpinBox(); self.iters.setRange(20, 50000); self.iters.setSingleStep(100); self.iters.setValue(S("iters", 1200, int))
        self.batch = QtWidgets.QSpinBox(); self.batch.setRange(1, 64); self.batch.setValue(S("batch", 6, int))
        self.crop = QtWidgets.QSpinBox(); self.crop.setRange(64, 512); self.crop.setSingleStep(16); self.crop.setValue(S("crop", 192, int))
        self.lr = QtWidgets.QDoubleSpinBox(); self.lr.setDecimals(4); self.lr.setRange(0.0001, 0.1); self.lr.setSingleStep(0.0005); self.lr.setValue(S("lr", 0.003, float))
        self.holdout = QtWidgets.QSpinBox(); self.holdout.setRange(0, 20); self.holdout.setValue(S("holdout", 2, int))
        self.holdout.setToolTip("Số ảnh cuối (theo tên) giữ lại để kiểm tra, không dùng để học. "
                                "Bị bỏ qua nếu bạn đã chọn ảnh kiểm tra cụ thể.")
        self.val_names = [n for n in S("val", "").split("|") if n]
        self.val_btn = QtWidgets.QPushButton()
        self.val_btn.clicked.connect(self.pick_val)
        self.use_init = QtWidgets.QCheckBox("Huấn luyện tiếp từ trọng số có sẵn (fine-tune):")
        self.use_init.setChecked(S("use_init", False, bool))
        self.init = QtWidgets.QLineEdit(existing(S("init", ""), MODELS / "fastnet.pt"))
        self.out = QtWidgets.QLineEdit(S("out", str(MODELS / "fastnet_new.pt")))
        # thiet bi huan luyen: CPU / CUDA / Intel XPU (iGPU) - chi hien cai PyTorch tren may nay thay duoc
        self.device = QtWidgets.QComboBox()
        self.device.addItem("Tự động (CUDA > Intel XPU > CPU)", "auto")
        for key, label in tf.available_devices():
            self.device.addItem(label, key)
        k = self.device.findData(S("device", "cpu"))
        self.device.setCurrentIndex(k if k >= 0 else self.device.findData("cpu"))
        self.bench_btn = QtWidgets.QPushButton("Đo tốc độ")
        self.bench_btn.setToolTip("Chạy thử vài bước huấn luyện trên từng thiết bị (dữ liệu giả) để xem thiết bị nào nhanh nhất")
        self.bench_btn.clicked.connect(self.bench)
        self.bench_worker = None
        dev_row = QtWidgets.QWidget()
        dr = QtWidgets.QHBoxLayout(dev_row)
        dr.setContentsMargins(0, 0, 0, 0)
        dr.addWidget(self.device, 1)
        dr.addWidget(self.bench_btn)
        self.dev_hint = QtWidgets.QLabel("")
        self.dev_hint.setWordWrap(True)
        self.dev_hint.setStyleSheet("color:#666;")
        self.dev_hint.setTextInteractionFlags(Qt.TextSelectableByMouse)
        if len(tf.available_devices()) == 1:
            ver = torch.__version__
            self.dev_hint.setText(
                f"Chỉ có CPU: PyTorch {ver} không thấy GPU/iGPU. Để huấn luyện bằng iGPU/GPU Intel, cài bản XPU:<br>"
                f"<code>pip install torch=={ver.split('+')[0]}+xpu --index-url https://download.pytorch.org/whl/xpu</code><br>"
                "rồi mở lại ứng dụng (cần driver Intel mới; iGPU đời cũ có thể không được hỗ trợ — bấm “Đo tốc độ” để kiểm tra).")
            self.dev_hint.setTextFormat(Qt.RichText)
        form = QtWidgets.QFormLayout()
        form.addRow("Thiết bị huấn luyện:", dev_row)
        form.addRow("Số bước (iters):", self.iters)
        form.addRow("Batch:", self.batch)
        form.addRow("Cạnh ô cắt (crop):", self.crop)
        form.addRow("Learning rate:", self.lr)
        form.addRow("Ảnh giữ lại kiểm tra:", self.holdout)
        form.addRow("", self.val_btn)
        form.addRow(self.use_init, path_row(self.init, "file", self, "Trọng số (*.pt)"))
        form.addRow("Lưu thành:", path_row(self.out, "file", self, "Trọng số (*.pt)", save=True))
        self.start = QtWidgets.QPushButton("▶ Bắt đầu huấn luyện")
        self.stop = QtWidgets.QPushButton("■ Dừng"); self.stop.setEnabled(False)
        self.apply = QtWidgets.QPushButton("Đặt làm fastnet.pt…"); self.apply.setEnabled(False)
        self.start.clicked.connect(self.run)
        self.stop.clicked.connect(self._stop)
        self.apply.clicked.connect(self.make_default)
        btns = QtWidgets.QHBoxLayout()
        for b in (self.start, self.stop, self.apply):
            btns.addWidget(b)
        self.bar = QtWidgets.QProgressBar()
        self.plot = LossPlot()
        self.log = log_box()
        self.result = QtWidgets.QLabel("")
        self.result.setWordWrap(True)
        self.table = readonly_table(["Ngưỡng", "Lệch số lượng TB", "Bias", "Lệch tối đa", "F1 vị trí"])
        self.table.setMaximumHeight(170)

        left = QtWidgets.QVBoxLayout()
        left.addLayout(form)
        if self.dev_hint.text():
            left.addWidget(self.dev_hint)
        left.addLayout(btns); left.addWidget(self.bar)
        left.addWidget(self.result); left.addWidget(self.table)
        right = QtWidgets.QVBoxLayout()
        right.addWidget(QtWidgets.QLabel("Loss"))
        right.addWidget(self.plot)
        right.addWidget(QtWidgets.QLabel("Nhật ký"))
        right.addWidget(self.log, 1)
        left_w = QtWidgets.QWidget(); left_w.setLayout(left)
        left_w.setMinimumWidth(400); left_w.setMaximumWidth(540)
        lay = QtWidgets.QHBoxLayout(self)
        lay.addWidget(left_w)
        lay.addLayout(right, 1)
        self._val_label()

    def save_settings(self):
        s = self.settings
        for k, v in (("iters", self.iters.value()), ("batch", self.batch.value()), ("crop", self.crop.value()),
                     ("lr", self.lr.value()), ("holdout", self.holdout.value()), ("val", "|".join(self.val_names)),
                     ("use_init", self.use_init.isChecked()), ("init", self.init.text()), ("out", self.out.text()),
                     ("device", self.device.currentData())):
            s.setValue("train/" + k, v)

    # ---- chon anh kiem tra
    def _val_label(self):
        if self.val_names:
            self.val_btn.setText(f"Ảnh kiểm tra: đã chọn {len(self.val_names)} ảnh (bấm để đổi)")
        else:
            self.val_btn.setText("Chọn ảnh kiểm tra cụ thể… (đang dùng: N ảnh cuối)")
        self.holdout.setEnabled(not self.val_names)

    def pick_val(self):
        images, labels = self.get_paths()
        names = [p[0].stem for p in tf.find_pairs(images, labels, log=lambda s: None)]
        if not names:
            QtWidgets.QMessageBox.information(self, "Chưa có dữ liệu", "Chưa thấy cặp ảnh/nhãn nào. Kiểm tra tab Dữ liệu.")
            return
        dlg = ValDialog(self, names, self.val_names)
        if dlg.exec_() == QtWidgets.QDialog.Accepted:
            self.val_names = dlg.selected()
            self._val_label()

    # ---- chay
    def run(self):
        images, labels = self.get_paths()
        out = Path(self.out.text().strip() or str(MODELS / "fastnet_new.pt"))
        if out.resolve() == (MODELS / "fastnet.pt").resolve():
            ans = QtWidgets.QMessageBox.question(
                self, "Ghi đè fastnet.pt",
                "Ô 'Lưu thành' đang trỏ thẳng vào fastnet.pt (mô hình đang dùng).\n"
                "Nên lưu ra fastnet_new.pt rồi dùng nút “Đặt làm fastnet.pt”.\n\nVẫn ghi đè (bản cũ sẽ được sao lưu)?")
            if ans != QtWidgets.QMessageBox.Yes:
                return
            bak = backup_file(out)
            self.log.appendPlainText(f"Đã sao lưu {out.name} -> {bak.name}")
        a = dict(images=images, labels=labels, val=tuple(self.val_names), holdout=0 if self.val_names else self.holdout.value(),
                 iters=self.iters.value(), batch=self.batch.value(), crop=self.crop.value(), lr=self.lr.value(),
                 init=self.init.text() if self.use_init.isChecked() else None, out=str(out),
                 device=self.device.currentData())
        self.plot.clear(); self.log.clear(); self.table.setRowCount(0)
        self.bar.setValue(0); self.bar.setMaximum(a["iters"]); self.result.setText("Đang chuẩn bị dữ liệu…")
        self.start.setEnabled(False); self.stop.setEnabled(True); self.apply.setEnabled(False)
        self.bench_btn.setEnabled(False)

        def job(w):
            return tf.run_training(a["images"], a["labels"], val=a["val"], holdout=a["holdout"], iters=a["iters"],
                                   batch=a["batch"], crop=a["crop"], lr=a["lr"], init=a["init"], out=a["out"],
                                   log=w.log.emit, progress=w.progress.emit, should_stop=w.should_stop,
                                   status=w.status.emit, device=a["device"])

        self.worker = Worker(job, self)
        self.worker.log.connect(self.log.appendPlainText)
        self.worker.status.connect(self.result.setText)
        self.worker.progress.connect(self._progress)
        self.worker.done.connect(self._done)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _stop(self):
        if self.worker is not None and self.worker.isRunning():
            self.worker.stop()
            self.stop.setEnabled(False)
            self.result.setText("Đang dừng…")

    def _progress(self, it, total, loss):
        self.bar.setMaximum(total); self.bar.setValue(it)
        self.plot.add(loss)
        self.result.setText(f"Bước {it}/{total} — loss {loss:.5f}")

    def _finish(self):
        self.start.setEnabled(True); self.stop.setEnabled(False); self.bench_btn.setEnabled(True)

    def bench(self):
        """Do toc do huan luyen tren moi thiet bi (du lieu gia), de chon thiet bi nhanh nhat."""
        b, c = self.batch.value(), self.crop.value()
        self.bench_btn.setEnabled(False); self.start.setEnabled(False)
        self.log.appendPlainText(f"Đo tốc độ huấn luyện (batch {b}, crop {c}) — iGPU Intel lần đầu có thể mất vài phút để biên dịch kernel (các lần sau dùng cache nên nhanh)…")
        self.result.setText("Đang đo tốc độ các thiết bị…")

        def job(w):
            return tf.benchmark_devices(b, c, log=w.log.emit, should_stop=w.should_stop)

        self.bench_worker = Worker(job, self)
        self.bench_worker.log.connect(self.log.appendPlainText)
        self.bench_worker.done.connect(self._bench_done)
        self.bench_worker.failed.connect(self._failed)
        self.bench_worker.start()

    def _bench_done(self, rows):
        self._finish()
        good = [r for r in rows if r["sps"]]
        parts = []
        for r in rows:
            if r["sps"]:
                sp = r.get("speedup")
                parts.append(f"{r['label']}: {r['sps']:.2f} bước/s" + (f" ({sp:.1f}×)" if sp and r["key"] != "cpu" else ""))
            else:
                parts.append(f"{r['label']}: lỗi ({r['error']})")
        best = max(good, key=lambda r: r["sps"])["key"] if good else "cpu"
        self.result.setText("Tốc độ huấn luyện — " + " • ".join(parts) + f"\n→ nhanh nhất: {best.upper()}")
        k = self.device.findData(best)
        if k >= 0:
            self.device.setCurrentIndex(k)

    def _done(self, res):
        self._finish()
        self.last_out = res["out"]
        if res["stopped"]:
            self.result.setText(f"⏹ Đã dừng — đã lưu bản dở dang: {res['out']}\n(bỏ qua bước đánh giá)")
            return
        self.apply.setEnabled(True)
        src = res["val_res"] or res["train_res"]
        if src is None:
            self.result.setText(f"✅ Đã lưu {res['out']}. Bước đánh giá bị bỏ qua theo yêu cầu dừng.")
            return
        for thr, (errs, f1) in src.items():
            add_row(self.table, [f"{thr:.2f}", f"{np.abs(errs).mean():.2f}", f"{errs.mean():+.2f}",
                                 int(np.abs(errs).max()), f"{f1:.3f}"], bad=(thr == res["best"]))
        which = "tập kiểm tra (chưa thấy)" if res["val_res"] else "tập huấn luyện — nên chọn ảnh kiểm tra"
        self.result.setText(f"✅ Xong — đã lưu {res['out']}\nNgưỡng gợi ý: {res['best']} (đánh giá trên {which}). "
                            "Đã điền sang tab Thử nhanh.")
        self.trained.emit(res["out"], float(res["best"]))

    def _failed(self, tb):
        self._finish()
        self.log.appendPlainText(tb)
        self.result.setText("❌ Lỗi: " + tb.strip().splitlines()[-1])

    def make_default(self):
        if not self.last_out or not Path(self.last_out).is_file():
            return
        dst = MODELS / "fastnet.pt"
        ok = QtWidgets.QMessageBox.question(
            self, "Đặt làm fastnet.pt",
            f"Thay {dst.name} bằng {Path(self.last_out).name}?\nBản cũ sẽ được sao lưu cạnh đó.")
        if ok != QtWidgets.QMessageBox.Yes:
            return
        bak = backup_file(dst)
        if bak:
            self.log.appendPlainText(f"Đã sao lưu bản cũ: {bak.name}")
        shutil.copy2(self.last_out, dst)
        self.log.appendPlainText(f"Đã đặt {Path(self.last_out).name} làm {dst.name}")

    def busy(self):
        return self.worker is not None and self.worker.isRunning()


# --------------------------------------------------------------------------- tab 4: openvino
class OpenVINOTab(QtWidgets.QWidget):
    converted = pyqtSignal(str)                  # duong dan .xml vua tao -> tab Thu nhanh dung ngay

    def __init__(self, settings):
        super().__init__()
        self.settings, self.worker = settings, None
        S = lambda k, d, c=str: sget(settings, "ov/" + k, d, c)
        self.weights = QtWidgets.QLineEdit(existing(S("weights", ""), MODELS / "fastnet.pt"))
        self.prec = QtWidgets.QComboBox(); self.prec.addItems(["FP16 (file nhỏ)", "FP32"])
        self.prec.setCurrentIndex(min(S("prec", 0, int), 1))
        self.out = QtWidgets.QLineEdit(S("out", ""))
        self.out.setPlaceholderText("để trống = models/openvino_fastnet/<tên>_fp16.xml")
        devs = fo.available_devices()
        self.devs = QtWidgets.QLabel("Thiết bị OpenVINO: " + (", ".join(devs) if devs else "không có (chưa cài openvino?)"))
        form = QtWidgets.QFormLayout()
        form.addRow("Trọng số PyTorch (.pt):", path_row(self.weights, "file", self, "Trọng số (*.pt)"))
        form.addRow("Độ chính xác lưu:", self.prec)
        form.addRow("File IR đầu ra (.xml):", path_row(self.out, "file", self, "OpenVINO IR (*.xml)", save=True))
        self.btn = QtWidgets.QPushButton("Chuyển sang OpenVINO + đo tốc độ")
        self.btn.clicked.connect(self.run)
        self.table = readonly_table(["Thiết bị", "Tên", "Biên dịch (s)", "ms / ảnh 768×768", "Lệch tối đa", "Nhanh gấp"])
        self.log = log_box()
        self.status = QtWidgets.QLabel("")
        self.status.setWordWrap(True)
        note = QtWidgets.QLabel("Sau khi chuyển, đường dẫn .xml được điền sẵn sang tab “Thử nhanh”; hoặc dùng: "
                                "python fast_counter.py <ảnh> --weights <file .xml> --device GPU. "
                                "iGPU được ép chạy f32 để cho kết quả đếm khớp PyTorch.")
        note.setWordWrap(True)
        lay = QtWidgets.QVBoxLayout(self)
        lay.addLayout(form); lay.addWidget(self.devs); lay.addWidget(self.btn); lay.addWidget(self.status)
        lay.addWidget(self.table); lay.addWidget(self.log, 1); lay.addWidget(note)

    def save_settings(self):
        for k, v in (("weights", self.weights.text()), ("prec", self.prec.currentIndex()), ("out", self.out.text())):
            self.settings.setValue("ov/" + k, v)

    def run(self):
        w, fp16, out = self.weights.text().strip(), self.prec.currentIndex() == 0, self.out.text().strip() or None
        self.table.setRowCount(0); self.log.clear(); self.btn.setEnabled(False)
        self.status.setText("Đang chuyển và đo… (lần đầu biên dịch iGPU mất ~20 giây)")

        def job(wk):
            xml = fo.convert(w, out, fp16=fp16, log=wk.log.emit)
            return xml, fo.benchmark(w, xml, log=wk.log.emit)

        self.worker = Worker(job, self)
        self.worker.log.connect(self.log.appendPlainText)
        self.worker.done.connect(self._done)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _done(self, res):
        xml, rows = res
        self.btn.setEnabled(True)
        base = next((r["ms"] for r in rows if r["name"] == "torch"), None)
        for r in rows:
            if r.get("error"):
                add_row(self.table, [r["device"], r["name"], "", "", "", r["error"]], bad=True)
            else:
                add_row(self.table, [r["device"], r["name"], f'{r["compile_s"]:.1f}', f'{r["ms"]:.0f}',
                                     f'{r["diff"]:.4f}', "" if r["name"] == "torch" or not base else f'{base / r["ms"]:.1f}×'])
        self.status.setText(f"✅ Đã lưu IR: {xml}  (đã điền sang tab Thử nhanh)")
        self.converted.emit(str(xml))

    def _failed(self, tb):
        self.btn.setEnabled(True)
        self.log.appendPlainText(tb)
        self.status.setText("❌ Lỗi: " + tb.strip().splitlines()[-1])

    def busy(self):
        return self.worker is not None and self.worker.isRunning()


# --------------------------------------------------------------------------- tab 5: thu nhanh
def first_image():
    imgs = fc.collect_images([ROOT])
    return str(imgs[0]) if imgs else ""


class PreviewTab(QtWidgets.QWidget):
    def __init__(self, settings):
        super().__init__()
        self.settings = settings
        self.worker, self._net, self._key = None, None, None
        S = lambda k, d, c=str: sget(settings, "preview/" + k, d, c)
        img = S("image", "")
        self.image = QtWidgets.QLineEdit(img if img and Path(img).is_file() else first_image())
        self.weights = QtWidgets.QLineEdit(existing(S("weights", ""), MODELS / "fastnet.pt"))
        self.device = QtWidgets.QComboBox(); self.device.addItems(["AUTO", "GPU", "CPU"])
        self.device.setCurrentText(S("device", "AUTO"))
        self.thr = QtWidgets.QDoubleSpinBox(); self.thr.setRange(0.1, 0.99); self.thr.setSingleStep(0.05)
        self.thr.setValue(S("thr", 0.8, float))
        self.expected = QtWidgets.QSpinBox(); self.expected.setRange(0, 5000); self.expected.setSpecialValueText("không")
        form = QtWidgets.QFormLayout()
        form.addRow("Ảnh:", path_row(self.image, "file", self, "Ảnh (*.bmp *.png *.jpg *.jpeg *.tif *.tiff)"))
        form.addRow("Trọng số (.pt hoặc .xml):", path_row(self.weights, "file", self, "Trọng số (*.pt *.xml)"))
        form.addRow("Thiết bị (chỉ cho .xml):", self.device)
        form.addRow("Ngưỡng đỉnh:", self.thr)
        form.addRow("Số đúng (tuỳ chọn):", self.expected)
        self.btn = QtWidgets.QPushButton("▶ Đếm")
        self.btn.clicked.connect(self.run)
        self.result = QtWidgets.QLabel("")
        self.result.setWordWrap(True)
        self.view = ImageView()
        side = QtWidgets.QVBoxLayout()
        side.addLayout(form); side.addWidget(self.btn); side.addWidget(self.result); side.addStretch(1)
        side_w = QtWidgets.QWidget(); side_w.setLayout(side); side_w.setMaximumWidth(520)
        lay = QtWidgets.QHBoxLayout(self)
        lay.addWidget(side_w); lay.addWidget(self.view, 1)

    def save_settings(self):
        for k, v in (("image", self.image.text()), ("weights", self.weights.text()),
                     ("device", self.device.currentText()), ("thr", self.thr.value())):
            self.settings.setValue("preview/" + k, v)

    def run(self):
        img, w, dev, thr, exp = (self.image.text().strip(), self.weights.text().strip(),
                                 self.device.currentText(), self.thr.value(), self.expected.value())
        key = (w, dev)
        cached = self._net if key == self._key else None
        self.btn.setEnabled(False)
        self.result.setText("Đang chạy…")

        def job(wk):
            gray = cv2.imread(img, cv2.IMREAD_GRAYSCALE)
            if gray is None:
                raise ValueError(f"Không đọc được ảnh: {img}")
            net, t_load = cached, 0.0
            if net is None:
                t = time.perf_counter()
                net = fc.load_net(w, dev)
                fc.count(np.zeros((300, 300), np.uint8), net)          # khởi động / biên dịch
                t_load = time.perf_counter() - t
            t = time.perf_counter()
            pts = fc.count(gray, net, thr)
            t_det = time.perf_counter() - t
            return dict(net=net, key=key, pts=pts, vis=fc.draw(gray, pts), t_load=t_load, t_det=t_det)

        self.worker = Worker(job, self)
        self.worker.done.connect(lambda r: self._done(r, exp))
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _done(self, r, exp):
        self.btn.setEnabled(True)
        self._net, self._key = r["net"], r["key"]
        n = len(r["pts"])
        self.view.set_image(r["vis"])
        msg = f"<b>{n} sợi</b> — đếm mất {r['t_det']:.3f} s" + (f" (nạp mô hình {r['t_load']:.1f} s)" if r["t_load"] else "")
        if exp:
            msg += f" — đúng {exp}, lệch {n - exp:+d}"
        self.result.setText(msg)

    def _failed(self, tb):
        self.btn.setEnabled(True)
        self.result.setText("❌ Lỗi: " + tb.strip().splitlines()[-1])

    def busy(self):
        return self.worker is not None and self.worker.isRunning()


# --------------------------------------------------------------------------- tab 6: dem hang loat
class BatchTab(QtWidgets.QWidget):
    """Dem ca thu muc anh: bang ket qua, thong ke do on dinh, so voi so dung, luu anh, xuat CSV."""

    def __init__(self, settings, get_paths):
        super().__init__()
        self.settings, self.get_paths = settings, get_paths
        self.worker, self._net, self._key, self.results = None, None, None, []
        S = lambda k, d, c=str: sget(settings, "batch/" + k, d, c)
        self.folder = QtWidgets.QLineEdit(S("folder", ""))
        self.folder.setPlaceholderText("để trống = thư mục ảnh ở tab Dữ liệu")
        self.weights = QtWidgets.QLineEdit(existing(S("weights", ""), MODELS / "fastnet.pt"))
        self.device = QtWidgets.QComboBox(); self.device.addItems(["AUTO", "GPU", "CPU"])
        self.device.setCurrentText(S("device", "AUTO"))
        self.thr = QtWidgets.QDoubleSpinBox(); self.thr.setRange(0.1, 0.99); self.thr.setSingleStep(0.05)
        self.thr.setValue(S("thr", 0.8, float))
        self.expected = QtWidgets.QSpinBox(); self.expected.setRange(0, 5000); self.expected.setSpecialValueText("không")
        self.expected.setValue(S("expected", 0, int))
        self.expected.setToolTip("Số đúng chung cho mọi ảnh (cùng một sợi dây). Bỏ trống nếu không có.")
        self.exp_file = QtWidgets.QLineEdit(S("exp_file", ""))
        self.exp_file.setPlaceholderText("tuỳ chọn: CSV 2 cột  tên_ảnh,số_đúng  (ưu tiên hơn số chung)")
        self.save_chk = QtWidgets.QCheckBox("Lưu ảnh đánh dấu vào thư mục:")
        self.save_chk.setChecked(S("save", False, bool))
        self.save_dir = QtWidgets.QLineEdit(S("save_dir", str(HERE / "output_batch")))
        form = QtWidgets.QFormLayout()
        form.addRow("Thư mục ảnh:", path_row(self.folder, "dir", self))
        form.addRow("Trọng số (.pt hoặc .xml):", path_row(self.weights, "file", self, "Trọng số (*.pt *.xml)"))
        form.addRow("Thiết bị (chỉ cho .xml):", self.device)
        form.addRow("Ngưỡng đỉnh:", self.thr)
        form.addRow("Số đúng chung:", self.expected)
        form.addRow("Số đúng theo ảnh:", path_row(self.exp_file, "file", self, "CSV (*.csv)"))
        form.addRow(self.save_chk, path_row(self.save_dir, "dir", self))
        self.start = QtWidgets.QPushButton("▶ Đếm cả thư mục")
        self.stop = QtWidgets.QPushButton("■ Dừng"); self.stop.setEnabled(False)
        self.export = QtWidgets.QPushButton("Xuất CSV…"); self.export.setEnabled(False)
        self.start.clicked.connect(self.run)
        self.stop.clicked.connect(self._stop)
        self.export.clicked.connect(self.export_csv)
        btns = QtWidgets.QHBoxLayout()
        for b in (self.start, self.stop, self.export):
            btns.addWidget(b)
        self.bar = QtWidgets.QProgressBar()
        self.summary = QtWidgets.QLabel("")
        self.summary.setWordWrap(True)
        self.summary.setTextFormat(Qt.RichText)
        self.table = readonly_table(["#", "Ảnh", "Số sợi", "Đúng", "Lệch", "ms", "Ghi chú"])
        self.table.setSelectionMode(QtWidgets.QAbstractItemView.SingleSelection)
        self.table.itemSelectionChanged.connect(self._show_selected)
        self.view = ImageView()

        left = QtWidgets.QVBoxLayout()
        left.addLayout(form); left.addLayout(btns); left.addWidget(self.bar)
        left.addWidget(self.summary); left.addWidget(self.table, 1)
        left_w = QtWidgets.QWidget(); left_w.setLayout(left)
        left_w.setMinimumWidth(460); left_w.setMaximumWidth(680)
        lay = QtWidgets.QHBoxLayout(self)
        lay.addWidget(left_w)
        lay.addWidget(self.view, 1)

    def save_settings(self):
        for k, v in (("folder", self.folder.text()), ("weights", self.weights.text()),
                     ("device", self.device.currentText()), ("thr", self.thr.value()),
                     ("expected", self.expected.value()), ("exp_file", self.exp_file.text()),
                     ("save", self.save_chk.isChecked()), ("save_dir", self.save_dir.text())):
            self.settings.setValue("batch/" + k, v)

    # ---- so dung theo anh
    def _read_expected(self):
        path = self.exp_file.text().strip()
        if not path:
            return {}
        out = {}
        with open(path, newline="", encoding="utf-8-sig") as fh:
            for row in csv.reader(fh):
                if len(row) >= 2 and row[1].strip().lstrip("-").isdigit():     # bo dong tieu de
                    out[Path(row[0].strip()).stem] = int(row[1])
        if not out:
            raise ValueError(f"Không đọc được số đúng nào từ {path} (cần cột 1 = tên ảnh, cột 2 = số).")
        return out

    # ---- chay
    def run(self):
        folder = self.folder.text().strip() or self.get_paths()[0]
        files = fc.collect_images([folder])
        if not files:
            self.summary.setText(f"❌ Không thấy ảnh nào trong: {folder}")
            return
        try:
            exp_map = self._read_expected()
        except Exception as e:
            self.summary.setText(f"❌ {e}")
            return
        w, dev, thr, common = self.weights.text().strip(), self.device.currentText(), self.thr.value(), self.expected.value()
        key = (w, dev)
        cached = self._net if key == self._key else None
        save_dir = Path(self.save_dir.text().strip()) if self.save_chk.isChecked() else None
        self.table.setRowCount(0)
        self.results, self.view._pix = [], None
        self.view.clear(); self.view.setText("Chọn một dòng để xem ảnh")
        self.bar.setMaximum(len(files)); self.bar.setValue(0)
        self.start.setEnabled(False); self.stop.setEnabled(True); self.export.setEnabled(False)
        self.summary.setText("Đang nạp mô hình…")

        def job(wk):
            if save_dir:
                save_dir.mkdir(parents=True, exist_ok=True)
            net, t_load = cached, 0.0
            if net is None:
                t = time.perf_counter()
                net = fc.load_net(w, dev)
                fc.count(np.zeros((300, 300), np.uint8), net)          # khởi động / biên dịch
                t_load = time.perf_counter() - t
            wk.status.emit(f"Đang đếm… (nạp mô hình {t_load:.1f} s)" if t_load else "Đang đếm…")
            for i, f in enumerate(files, 1):
                if wk.should_stop():
                    break
                gray = cv2.imread(str(f), cv2.IMREAD_GRAYSCALE)
                if gray is None:
                    wk.item.emit(dict(name=f.name, path=str(f), n=None, ms=0.0, exp=None, pts=None, note="không đọc được ảnh"))
                else:
                    t = time.perf_counter()
                    pts = fc.count(gray, net, thr)
                    ms = (time.perf_counter() - t) * 1000
                    note = ""
                    if save_dir:
                        cv2.imwrite(str(save_dir / f"{f.stem}_count.png"), fc.draw(gray, pts))
                        note = "đã lưu ảnh"
                    exp = exp_map.get(f.stem, common or None)
                    wk.item.emit(dict(name=f.name, path=str(f), n=len(pts), ms=ms, exp=exp, pts=pts, note=note))
                wk.progress.emit(i, len(files), 0.0)
            return dict(net=net, key=key, stopped=wk.should_stop())

        self.worker = Worker(job, self)
        self.worker.status.connect(self.summary.setText)
        self.worker.item.connect(self._on_item)
        self.worker.progress.connect(lambda i, n, _: self.bar.setValue(i))
        self.worker.done.connect(self._done)
        self.worker.failed.connect(self._failed)
        self.worker.start()

    def _stop(self):
        if self.worker is not None and self.worker.isRunning():
            self.worker.stop()
            self.stop.setEnabled(False)

    def _on_item(self, d):
        self.results.append(d)
        diff = "" if d["n"] is None or d["exp"] is None else f'{d["n"] - d["exp"]:+d}'
        bad = d["n"] is None or (diff not in ("", "+0"))
        add_row(self.table, [len(self.results), d["name"], "—" if d["n"] is None else d["n"],
                             "" if d["exp"] is None else d["exp"], diff, f'{d["ms"]:.0f}', d["note"]], bad=bad)
        self.table.scrollToBottom()
        self._update_summary(final=False)

    def stats(self):
        """Thong ke tren cac anh dem duoc. Tra ve dict hoac None."""
        ok = [r for r in self.results if r["n"] is not None]
        if not ok:
            return None
        n = np.array([r["n"] for r in ok], float)
        st = dict(count=len(ok), mean=n.mean(), min=int(n.min()), max=int(n.max()), spread=int(n.max() - n.min()),
                  std=float(n.std()), ms=float(np.mean([r["ms"] for r in ok])))
        with_exp = [r for r in ok if r["exp"] is not None]
        if with_exp:
            e = np.array([r["n"] - r["exp"] for r in with_exp], float)
            st.update(exp_n=len(with_exp), mae=float(np.abs(e).mean()), bias=float(e.mean()), exact=int((e == 0).sum()))
        return st

    def _update_summary(self, final):
        st = self.stats()
        if st is None:
            return
        txt = (f"<b>{st['count']}/{self.bar.maximum()} ảnh</b> • số sợi TB <b>{st['mean']:.1f}</b> "
               f"(min {st['min']}, max {st['max']}, <b>dao động {st['spread']}</b>, độ lệch chuẩn {st['std']:.2f}) "
               f"• {st['ms']:.0f} ms/ảnh")
        if "mae" in st:
            txt += (f"<br>So với số đúng ({st['exp_n']} ảnh): sai lệch TB <b>{st['mae']:.2f}</b>, "
                    f"lệch trung bình {st['bias']:+.2f}, đúng hoàn toàn <b>{st['exact']}/{st['exp_n']}</b> ảnh")
        self.summary.setText(txt)

    def _done(self, res):
        self._net, self._key = res["net"], res["key"]
        self.start.setEnabled(True); self.stop.setEnabled(False)
        self.export.setEnabled(bool(self.results))
        self._update_summary(final=True)
        if res["stopped"]:
            self.summary.setText(self.summary.text() + "<br>⏹ Đã dừng theo yêu cầu.")

    def _failed(self, tb):
        self.start.setEnabled(True); self.stop.setEnabled(False)
        self.export.setEnabled(bool(self.results))
        self.summary.setText("❌ Lỗi: " + tb.strip().splitlines()[-1])

    def _show_selected(self):
        rows = self.table.selectionModel().selectedRows()
        if not rows or rows[0].row() >= len(self.results):
            return
        r = self.results[rows[0].row()]
        if r["pts"] is None:
            return
        gray = cv2.imread(r["path"], cv2.IMREAD_GRAYSCALE)
        if gray is not None:
            self.view.set_image(fc.draw(gray, r["pts"]))

    def export_csv(self, path=None):
        if not self.results:
            return None
        if not path:
            path, _ = QtWidgets.QFileDialog.getSaveFileName(self, "Xuất kết quả", str(HERE / "batch_results.csv"), "CSV (*.csv)")
        if not path:
            return None
        with open(path, "w", newline="", encoding="utf-8-sig") as fh:     # utf-8-sig: Excel mo dung tieng Viet
            wr = csv.writer(fh)
            wr.writerow(["image", "count", "expected", "diff", "ms", "note"])
            for r in self.results:
                diff = "" if r["n"] is None or r["exp"] is None else r["n"] - r["exp"]
                wr.writerow([r["name"], "" if r["n"] is None else r["n"], "" if r["exp"] is None else r["exp"],
                             diff, f'{r["ms"]:.1f}', r["note"]])
        self.summary.setText(self.summary.text() + f"<br>💾 Đã xuất {path}")
        return path

    def busy(self):
        return self.worker is not None and self.worker.isRunning()


# --------------------------------------------------------------------------- cua so chinh
class MainWindow(QtWidgets.QMainWindow):
    def __init__(self, settings=None):
        super().__init__()
        self.setWindowTitle("FastNet — huấn luyện & OpenVINO")
        scr = QtWidgets.QApplication.primaryScreen().availableGeometry()
        self.resize(min(1180, int(scr.width() * 0.95)), min(780, int(scr.height() * 0.92)))
        self.settings = settings or default_settings()
        geo = self.settings.value("window/geometry")
        if geo is not None:
            self.restoreGeometry(geo)
        self.data = DataTab(self.settings)
        self.label = LabelTab(self.settings, self.data.paths)
        self.train = TrainTab(self.settings, self.data.paths)
        self.ov = OpenVINOTab(self.settings)
        self.preview = PreviewTab(self.settings)
        self.batch = BatchTab(self.settings, self.data.paths)
        self.tabs = QtWidgets.QTabWidget()
        self.tabs.addTab(self.data, "1. Dữ liệu")
        self.tabs.addTab(self.label, "2. Dán nhãn")
        self.tabs.addTab(self.train, "3. Huấn luyện")
        self.tabs.addTab(self.ov, "4. OpenVINO")
        self.tabs.addTab(self.preview, "5. Thử nhanh")
        self.tabs.addTab(self.batch, "6. Đếm hàng loạt")
        self.setCentralWidget(self.tabs)
        self.statusBar().showMessage(f"PyTorch {torch.__version__} • OpenVINO: "
                                     f"{', '.join(fo.available_devices()) or 'không có'}")
        # lien ket giua cac tab
        self.label.saved.connect(self.data.mark_stale)
        self.tabs.currentChanged.connect(self._tab_changed)
        self.train.trained.connect(self._on_trained)
        self.ov.converted.connect(self._on_converted)

    def _tab_changed(self, i):
        if i == 0 and self.data.stale and not (self.data.worker and self.data.worker.isRunning()):
            self.data.run_check()

    def _on_trained(self, weights, best_thr):
        for tab in (self.preview, self.batch):
            tab.weights.setText(weights)
            tab.device.setCurrentText("AUTO")
            tab.thr.setValue(best_thr)
        self.statusBar().showMessage(f"Đã điền {Path(weights).name} + ngưỡng {best_thr} sang tab Thử nhanh và Đếm hàng loạt", 10000)

    def _on_converted(self, xml):
        self.preview.weights.setText(xml)
        self.batch.weights.setText(xml)
        self.statusBar().showMessage(f"Đã điền {Path(xml).name} sang tab Thử nhanh và Đếm hàng loạt", 10000)

    def _save_settings(self):
        for tab in (self.train, self.ov, self.preview, self.batch):
            tab.save_settings()
        self.label.save_settings()
        self.settings.setValue("window/geometry", self.saveGeometry())
        self.settings.setValue("window/tab", self.tabs.currentIndex())
        self.settings.sync()

    def closeEvent(self, e):
        if not self.label.maybe_save():
            e.ignore()
            return
        running = Worker.running()
        loud = [w for w in running if not w.quiet]            # tac vu dai: phai hoi; tac vu ngan: cho xong roi thoat
        if loud:
            ans = QtWidgets.QMessageBox.question(
                self, "Còn tác vụ đang chạy",
                f"Đang có {len(loud)} tác vụ nền (huấn luyện / chuyển OpenVINO / đếm…).\nThoát sẽ dừng chúng. Thoát?")
            if ans != QtWidgets.QMessageBox.Yes:
                e.ignore()
                return
        if running:
            for w in running:
                w.stop()
            deadline = time.time() + 20
            for w in running:
                w.wait(max(int((deadline - time.time()) * 1000), 100))
        self._save_settings()
        e.accept()
        if Worker.running():
            # Co tac vu khong dung duoc (vd bien dich OpenVINO): thoat cung, neu khong Qt se abort vi huy luong dang chay.
            sys.stdout.flush()
            sys.stderr.flush()
            os._exit(0)


def selftest(image, report):
    """Tu kiem tra ban dong goi: python/exe --selftest <anh> <bao_cao.json>. Tra ve 0 neu moi buoc dat.

    Chay qua moi duong quan trong (nap mo hinh, dem bang PyTorch va OpenVINO, chuyen .pt -> IR, huan luyen vai buoc,
    dung cua so Qt) de phat hien thieu DLL / thieu module sau khi dong goi hoac khi chuyen sang may khac.
    """
    import json
    import tempfile
    res = {"frozen": FROZEN, "app_dir": str(HERE), "python": sys.version.split()[0], "steps": []}

    def step(name, fn):
        t = time.perf_counter()
        try:
            val = fn()
            res["steps"].append(dict(name=name, ok=True, value=val, s=round(time.perf_counter() - t, 2)))
            return val
        except BaseException as e:
            res["steps"].append(dict(name=name, ok=False, error=f"{type(e).__name__}: {str(e)[:300]}",
                                     s=round(time.perf_counter() - t, 2)))
            return None

    tmp = Path(tempfile.mkdtemp(prefix="fastnet_selftest_"))
    gray = cv2.imread(str(image), cv2.IMREAD_GRAYSCALE)
    step("versions", lambda: dict(torch=torch.__version__, cv2=cv2.__version__, numpy=np.__version__,
                                  qt=QtCore.QT_VERSION_STR, openvino=__import__("openvino").__version__))
    step("read_image", lambda: list(gray.shape))
    pts = step("count_torch", lambda: fc.count(gray, fc.load_net(MODELS / "fastnet.pt")).tolist())
    if pts is not None:
        res["steps"][-1]["value"] = len(pts)
    devs = step("openvino_devices", fo.available_devices) or []
    xml = MODELS / "openvino_fastnet" / "fastnet_fp16.xml"
    for d in ["CPU"] + (["GPU"] if "GPU" in devs else []):
        step(f"count_openvino_{d}", lambda d=d: len(fc.count(gray, fo.load(xml, d))))
    new_xml = step("convert_pt_to_ir", lambda: str(fo.convert(MODELS / "fastnet.pt", tmp / "conv" / "t_fp16.xml",
                                                               log=lambda s: None)))
    if new_xml:
        step("count_converted_ir_CPU", lambda: len(fc.count(gray, fo.load(new_xml, "CPU"))))

    def train():
        (tmp / "img").mkdir(); (tmp / "lab").mkdir()
        dst = tmp / "img" / ("selftest" + Path(str(image)).suffix)
        shutil.copy2(str(image), dst)
        lp.write_points(tmp / "lab" / "selftest_points.csv", pts)
        r = tf.run_training(str(tmp / "img"), str(tmp / "lab"), iters=3, batch=2, crop=96,
                            out=str(tmp / "t.pt"), log=lambda s: None)
        return dict(saved=Path(r["out"]).is_file(), best=r["best"])
    if pts:
        step("train_3_steps", train)
    step("train_devices", lambda: [k for k, _ in tf.available_devices()])

    def window():
        app = QtWidgets.QApplication.instance() or QtWidgets.QApplication(sys.argv[:1])
        win = MainWindow(QSettings(str(tmp / "s.ini"), QSettings.IniFormat))
        win.show()
        app.processEvents()
        size = win.grab().size()
        n = win.tabs.count()
        win.close()
        return dict(tabs=n, w=size.width(), h=size.height())
    step("qt_main_window", window)

    res["ok"] = all(s["ok"] for s in res["steps"])
    Path(report).parent.mkdir(parents=True, exist_ok=True)
    Path(report).write_text(json.dumps(res, ensure_ascii=False, indent=2), encoding="utf-8")
    shutil.rmtree(tmp, ignore_errors=True)
    return 0 if res["ok"] else 1


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "--selftest":
        code = selftest(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else str(HERE / "selftest_report.json"))
        sys.stdout.flush()
        os._exit(code)                           # khong doi luong nen cua OpenVINO/Qt
    QtWidgets.QApplication.setAttribute(Qt.AA_EnableHighDpiScaling, True)
    QtWidgets.QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps, True)
    app = QtWidgets.QApplication(sys.argv)
    app.setStyle("Fusion")
    win = MainWindow()
    win.show()
    sys.exit(app.exec_())


if __name__ == "__main__":
    main()
