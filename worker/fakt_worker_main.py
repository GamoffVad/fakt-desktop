# -*- coding: utf-8 -*-
"""Entry point of the FAKT worker.

Started by the client as ``python.exe -I -X utf8 -u fakt_worker_main.py``.
In isolated mode (-I) the script directory is not on sys.path, so it is
added explicitly before importing the package.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fakt_worker.server import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
