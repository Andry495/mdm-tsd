"""Показать результаты извлечения элементов"""

import os
from pathlib import Path

OUTPUT_DIR = "извлеченные_элементы"
PICTOGRAMS_DIR = os.path.join(OUTPUT_DIR, "пиктограммы")
BRANDS_DIR = os.path.join(OUTPUT_DIR, "бренды")

print("=" * 80)
print("СТРУКТУРА ИЗВЛЕЧЕННЫХ ЭЛЕМЕНТОВ")
print("=" * 80)

if os.path.exists(PICTOGRAMS_DIR):
    print(f"\nПИКТОГРАММЫ ({PICTOGRAMS_DIR}):")
    print("-" * 80)
    pict_dirs = [d for d in os.listdir(PICTOGRAMS_DIR) if os.path.isdir(os.path.join(PICTOGRAMS_DIR, d))]
    for pict_dir in pict_dirs:
        pict_path = os.path.join(PICTOGRAMS_DIR, pict_dir)
        files = [f for f in os.listdir(pict_path) if f.endswith('.jpg')]
        print(f"\n  {pict_dir}: {len(files)} файлов")
        for f in files[:5]:
            print(f"    - {f}")
        if len(files) > 5:
            print(f"    ... и еще {len(files) - 5} файлов")

if os.path.exists(BRANDS_DIR):
    print(f"\n\nБРЕНДЫ ({BRANDS_DIR}):")
    print("-" * 80)
    brand_dirs = [d for d in os.listdir(BRANDS_DIR) if os.path.isdir(os.path.join(BRANDS_DIR, d))]
    if brand_dirs:
        for brand_dir in brand_dirs:
            brand_path = os.path.join(BRANDS_DIR, brand_dir)
            files = [f for f in os.listdir(brand_path) if f.endswith('.jpg')]
            print(f"\n  {brand_dir}: {len(files)} файлов")
            for f in files[:5]:
                print(f"    - {f}")
    else:
        print("  Каталоги брендов не созданы (бренды не определены)")

print("\n" + "=" * 80)
print("СТРУКТУРА КАТАЛОГОВ СОЗДАНА УСПЕШНО!")
print("=" * 80)
