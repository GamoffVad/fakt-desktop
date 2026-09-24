# -*- coding: utf-8 -*-
"""Запуск тестов worker на поставляемом (embeddable) Python: каталог worker добавляется в sys.path явно,
потому что python38._pth задаёт изолированный путь поиска модулей."""
import os
import sys
import unittest

WORKER_DIR = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, WORKER_DIR)
os.chdir(WORKER_DIR)

if __name__ == "__main__":
    suite = unittest.defaultTestLoader.discover(os.path.join(WORKER_DIR, "tests"), top_level_dir=WORKER_DIR)
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    sys.exit(0 if result.wasSuccessful() else 1)
