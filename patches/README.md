Fork patches apply to ramensoftware/windhawk at upstream commit:
b59b38cd77daec98830c0e5e2ad14a35c44f02a7

Apply from the windhawk clone:
  git -C windhawk apply ../patches/0001-windhawkxyz-fork.patch

Contents of 0001-windhawkxyz-fork.patch:
- AutomaticUpdates: pre-engine-start mod update + 23-24h timer (portable RunDaemon + service SvcInit)
- Fork identity rebrand to WindhawkXYZ (service name + service/app kernel objects, daemon mutex, window class, IPC events). Engine per-session namespace intentionally left as upstream (only matters for two engines running at once).
