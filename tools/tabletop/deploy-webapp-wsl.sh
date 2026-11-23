#!/bin/bash
# Quest Tabletop P5 web deploys to the LOCAL WSL web app (World Builder target; never 192.168.0.2).
# Same sync rules as ~/deploy.sh (excludes operator state and extracted assets):
#   1. backup the install's code (same excludes) to ~/deploy-backups/tabletop-<stamp>
#   2. graceful stop: sudo -n systemctl stop mangossuperui (a NOPASSWD rule allows exactly this on this machine)
#   3. rsync the Windows Release publish output into /opt/mangossuperui, then systemctl start
#   4. wait for /QuestAtlas/Zones to answer; on failure restore the backup and restart
# 2026-11-15: an earlier version restarted the service with kill -9, which aborted in-flight database writes; a MyISAM
# table in the characters DB was found crashed right after (repaired). Never kill the web app; stop it gracefully.
# Usage (from Windows): wsl -e bash /mnt/c/Users/nico/source/repos/MSUIClient/tools/tabletop/deploy-webapp-wsl.sh
set -euo pipefail
SRC="/mnt/c/Users/nico/source/repos/MangosSuperUI/MangosSuperUI/bin/Release/net8.0/publish"
DEST="/opt/mangossuperui"
EXCLUDES=(--exclude 'wwwroot/item_models/' --exclude 'wwwroot/minimap/' --exclude 'wwwroot/models/' --exclude 'wwwroot/icons/'
          --exclude '/server-config.json' --exclude '/data/' --exclude '/wwwroot/patches/' --exclude '/RaidPlans/'
          --exclude '/Rotations/' --exclude '/diagnostics/')
[ -s "$SRC/MangosSuperUI.dll" ] || { echo "ERROR: no publish output at $SRC"; exit 1; }
STAMP=$(date +%Y%m%dT%H%M%S)
BACKUP="$HOME/deploy-backups/tabletop-$STAMP"
mkdir -p "$BACKUP"
rsync -rlt "${EXCLUDES[@]}" "$DEST"/ "$BACKUP"/
echo "backup: $BACKUP"

answers() {
  for i in $(seq 1 60); do
    sleep 2
    if curl -s -m 5 -o /dev/null -w "%{http_code}" http://localhost:5000/QuestAtlas/Zones | grep -q 200; then return 0; fi
  done
  return 1
}

sudo -n /usr/bin/systemctl stop mangossuperui
rsync -rlt --delete "${EXCLUDES[@]}" "$SRC"/ "$DEST"/
sudo -n /usr/bin/systemctl start mangossuperui
if answers; then
  echo "deployed: $(stat -c %y "$DEST/MangosSuperUI.dll"), pid $(systemctl show mangossuperui -p MainPID --value)"
else
  echo "NEW BUILD DID NOT ANSWER - restoring $BACKUP"
  sudo -n /usr/bin/systemctl stop mangossuperui
  rsync -rlt --delete "${EXCLUDES[@]}" "$BACKUP"/ "$DEST"/
  sudo -n /usr/bin/systemctl start mangossuperui
  answers && echo "restored the previous build" || echo "RESTORE ALSO FAILED - check: systemctl status mangossuperui"
  exit 1
fi
