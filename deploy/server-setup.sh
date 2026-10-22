#!/usr/bin/env bash
# Ubuntu 24.04 sunucusunu bir kez hazırlar: Docker, güvenlik duvarı (22/80/443), swap, otomatik güvenlik güncellemeleri.
# Kullanım: sudo bash server-setup.sh
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then echo "sudo ile çalıştırın: sudo bash $0"; exit 1; fi
TARGET_USER="${SUDO_USER:-$(logname)}"

echo "==> Paketler güncelleniyor"
apt-get update -y
DEBIAN_FRONTEND=noninteractive apt-get upgrade -y
DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl git ufw unattended-upgrades

echo "==> Docker kuruluyor"
if ! command -v docker >/dev/null; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
systemctl enable --now docker
usermod -aG docker "$TARGET_USER"

echo "==> Güvenlik duvarı: yalnızca SSH, HTTP, HTTPS"
ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable

echo "==> 4 GB swap (4 GB RAM'de SQL Server + .NET derlemesi için)"
if ! swapon --show | grep -q /swapfile; then
  fallocate -l 4G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

echo "==> Saat dilimi"
timedatectl set-timezone Europe/Istanbul

echo "==> Otomatik güvenlik güncellemeleri"
dpkg-reconfigure -f noninteractive unattended-upgrades

echo
echo "Tamam. Docker grubunun geçerli olması için oturumu kapatıp yeniden SSH ile bağlanın."
