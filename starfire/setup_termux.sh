#!/data/data/com.termux/files/usr/bin/env bash
set -e
echo "============================================"
echo " STARFIRE SENSOR NODE -- TERMUX SETUP"
echo "============================================"

pkg update -y
pkg install -y python termux-api

pip install --upgrade pip
pip install websockets

echo ""
echo "SENSOR NODE -- SETUP COMPLETE"
echo "Run: python sensor_node.py"
