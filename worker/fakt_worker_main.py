# -*- coding: utf-8 -*-
"""Entry point of the FAKT worker.

Started by the client as ``python.exe -I -X utf8 -u fakt_worker_main.py``.
In isolated mode (-I) the script directory is not on sys.path, so it is
added explicitly before importing the package.
"""
import os
import sys

# numpy (imported by pandas) loads OpenBLAS, which starts one thread per logical CPU and commits
# ~32 MB for each of them. The worker does no linear algebra (pandas parses text with dtype=str),
# so one thread is enough; otherwise a worker on 24 logical CPUs holds ~740 MB of private bytes
# and 23 idle threads. An explicitly set value is kept. Must run before numpy is imported.
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fakt_worker.server import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
