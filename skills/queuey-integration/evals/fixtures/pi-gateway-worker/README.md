# Sensor gateway

.NET worker running on a Raspberry Pi 4 at each customer site, reading Modbus
sensors every 10 s and POSTing readings to the partner's URL over the site's
LTE router. Installed by `deploy/install.sh` as a systemd service. Known issue:
when LTE drops for more than the retry window, readings from that period are
lost.
