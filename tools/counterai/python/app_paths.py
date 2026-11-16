"""Thu muc goc cua ung dung.

Chay bang Python: thu muc chua file .py.  Da dong goi (PyInstaller): thu muc chua file .exe - noi dat
models/, samples/, settings.ini... de nguoi dung thay mo hinh ma khong phai dong goi lai.
"""
import sys
from pathlib import Path

FROZEN = bool(getattr(sys, "frozen", False))


def app_dir(module_file):
    return Path(sys.executable).resolve().parent if FROZEN else Path(module_file).resolve().parent
