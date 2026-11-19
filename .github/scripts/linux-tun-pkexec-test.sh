#!/bin/bash
# TUN mode for a normal user on Linux (#90): OnionHop starts only the tunnel core as root, through
# pkexec. Runs as root inside a Debian 13 container started with --cap-add NET_ADMIN and
# --device /dev/net/tun, so the tunnel routes the container's network, not the runner's. Sets up
# D-Bus and polkit with a rule that lets the test user use pkexec without a password, since there is
# no desktop to type one into, then runs the TUN smoke test as that user.
#
# The container also gets a firewall that drops incoming traffic, as ufw and firewalld do by
# default (#97). The tunnel must carry traffic behind it with the default stack (gVisor on Linux);
# with the Mixed stack it must not, which shows the firewall here is doing its part.
#
# Usage: linux-tun-pkexec-test.sh <cli dir>
set -eu

CLI_DIR="$1"
SCRIPTS="$(cd "$(dirname "$0")" && pwd)"

apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    ca-certificates openssl dbus polkitd pkexec iproute2 curl procps iptables >/dev/null

dbus-uuidgen --ensure
mkdir -p /run/dbus /etc/polkit-1/rules.d
dbus-daemon --system --fork
/usr/lib/polkit-1/polkitd --no-debug >/tmp/polkitd.log 2>&1 &

cat > /etc/polkit-1/rules.d/50-onionhop-test.rules <<'EOF'
polkit.addRule(function (action, subject) {
    if (action.id == "org.freedesktop.policykit.exec" && subject.user == "tester") {
        return polkit.Result.YES;
    }
});
EOF

useradd --create-home --shell /bin/bash tester

# polkitd needs a moment to come up on the bus.
for i in $(seq 1 30); do
    if su tester -c "pkexec /bin/true" >/dev/null 2>&1; then
        break
    fi
    sleep 1
done
if ! su tester -c "pkexec /bin/true"; then
    echo "::error::pkexec does not work in the test container"
    cat /tmp/polkitd.log
    exit 1
fi

# What "ufw default deny incoming" comes down to: loopback and replies are let in, nothing else.
iptables -A INPUT -i lo -j ACCEPT
iptables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
iptables -P INPUT DROP
if ip6tables -A INPUT -i lo -j ACCEPT 2>/dev/null; then
    ip6tables -A INPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
    ip6tables -P INPUT DROP
fi
echo "firewall: incoming traffic is dropped by default"
iptables -S INPUT

su tester -c "bash '$SCRIPTS/linux-cli-smoke.sh' '$CLI_DIR' tun"
su tester -c "bash '$SCRIPTS/linux-cli-smoke.sh' '$CLI_DIR' tun-mixed-blocked"
