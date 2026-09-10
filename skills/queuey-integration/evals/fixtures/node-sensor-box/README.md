# Plant collector

Node 20 service on an Ubuntu 22.04 industrial PC in the plant, installed as a
systemd unit. Reads OPC-UA tags every 5 s and POSTs them to the partner. The
plant Wi-Fi drops several times a day; whatever is in the in-memory queue when
the process restarts is gone. No .NET on the box and we would rather not add it.
