#!/usr/bin/env bash
# Дымовая проверка APK на эмуляторе с экраном как у Rokid AI Glasses (480×640):
# запуск, шаги свайпом/клавишами дужки, меню, экран «Сборка завершена». Скриншоты → rokid/ci/shots/.
set -euo pipefail
APK="$1"
OUT="$(dirname "$0")/shots"
mkdir -p "$OUT"
PKG=app.stark.rokid

adb wait-for-device
adb shell wm size 480x640
adb shell wm density 160
adb shell settings put system screen_off_timeout 600000 || true
adb shell input keyevent 82 || true          # разблокировать экран эмулятора
adb install -r -g "$APK"
adb logcat -c
adb shell am start -W -n $PKG/.MainActivity
sleep 6

shot() { adb exec-out screencap -p > "$OUT/$1.png"; echo "shot $1"; }
key() { adb shell input keyevent "$1"; sleep "${2:-1.5}"; }

shot 01-step1
key 22                                    # DPAD_RIGHT = следующий шаг
key 22
shot 02-step3
adb shell input swipe 100 320 400 320 150 # свайп вперёд по сенсору = следующий шаг
sleep 1.5
key 22
key 22
shot 03-step6-fasteners
key 22
shot 04-step7
key 82                                    # MENU = меню
shot 05-menu
key 22                                    # вниз по меню
key 22
key 66                                    # ОК: «Открыть пакет…»
shot 06-packages
key 4                                     # назад = закрыть меню
key 22
key 22
shot 07-finished
key 21                                    # DPAD_LEFT = назад к шагу 8
shot 08-step8

# Пакет кабелем: adb push в папку приложения открывается сам
adb shell mkdir -p /sdcard/Android/data/$PKG/files
adb push "$(dirname "$0")/../../samples/gearbox_module.stark" /sdcard/Android/data/$PKG/files/cable-test.stark
adb shell touch /sdcard/Android/data/$PKG/files/cable-test.stark
sleep 6
shot 09-cable-push

# Ссылка из QR Studio (stark://link) принимается и показывает статус подключения
adb shell am start -a android.intent.action.VIEW -d "'stark://link?u=http%3A%2F%2F10.0.2.2%3A8041&t=AbCdEfGhIjKlMnOpQrStUvWx'" $PKG
sleep 5
shot 10-studio-link

adb logcat -d > "$OUT/logcat.txt"
if grep -q "FATAL EXCEPTION" "$OUT/logcat.txt"; then
  echo "Приложение упало:"; grep -A 30 "FATAL EXCEPTION" "$OUT/logcat.txt"; exit 1
fi
adb shell pidof $PKG >/dev/null || { echo "Процесс приложения не запущен"; exit 1; }
echo "OK"
