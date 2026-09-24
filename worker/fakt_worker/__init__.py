# -*- coding: utf-8 -*-
"""FAKT worker: reads source files for the FAKT desktop client.

The worker is started as a child process and talks JSON lines over
stdin/stdout (see protocol/PROTOCOL.md). It only reads files: it never
modifies them and never touches the network, databases or secrets.
"""

__version__ = "1.0.0"
PROTOCOL_VERSION = 1
