#!/usr/bin/env sh
# Скачать открытые STEP-сборки для проверки Studio (в репозиторий не кладём, у них свои лицензии).
set -e
mkdir -p samples/open && cd samples/open
# AS1: эталонная сборка OpenCascade / CAx-IF (плита, 2 уголка, тяга, 6 болтов, 8 гаек), 18 деталей
curl -fL -o as1-oc-214.stp https://raw.githubusercontent.com/tpaviot/pythonocc-demos/master/assets/models/as1-oc-214.stp
# Двухступенчатый планетарный редуктор NEMA17 с рычагами, 20 деталей (MotionProof, лицензия MIT)
curl -fL -o nema17_planetary_arm.step https://raw.githubusercontent.com/CapedCrusader77/MotionProof/HEAD/benchmarks/data/reference_design/geometry/assembly.step
ls -la
