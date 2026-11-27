"""Загрузка информации о брендах и пиктограммах из исходных материалов"""

import os
from pathlib import Path

SOURCE_DIR = r"\\bytplast.local\public\_Проекты\Честный_знак\02_Исходные_материалы\Печать_этикеток"
BRANDS_DIR = os.path.join(SOURCE_DIR, "Бренды")
PICTOGRAMS_DIR = os.path.join(SOURCE_DIR, "Пиктограммы")

def load_brands():
    """Загружает список брендов из каталога"""
    brands = {}
    
    if os.path.exists(BRANDS_DIR):
        for file in os.listdir(BRANDS_DIR):
            if file.lower().endswith(('.png', '.jpg', '.jpeg', '.svg')):
                # Извлекаем название бренда из имени файла
                brand_name = os.path.splitext(file)[0]
                brand_path = os.path.join(BRANDS_DIR, file)
                brands[brand_name.lower()] = {
                    'name': brand_name,
                    'path': brand_path,
                    'filename': file
                }
    
    return brands

def load_pictograms():
    """Загружает список пиктограмм из каталога"""
    pictograms = {}
    
    if os.path.exists(PICTOGRAMS_DIR):
        for file in os.listdir(PICTOGRAMS_DIR):
            if file.lower().endswith(('.png', '.jpg', '.jpeg', '.svg')):
                # Извлекаем название пиктограммы из имени файла
                pictogram_name = os.path.splitext(file)[0]
                pictogram_path = os.path.join(PICTOGRAMS_DIR, file)
                pictograms[pictogram_name.lower()] = {
                    'name': pictogram_name,
                    'path': pictogram_path,
                    'filename': file
                }
    
    return pictograms

if __name__ == "__main__":
    print("Загрузка брендов и пиктограмм...")
    
    brands = load_brands()
    print(f"\nНайдено брендов: {len(brands)}")
    for brand_key, brand_info in brands.items():
        print(f"  - {brand_info['name']}")
    
    pictograms = load_pictograms()
    print(f"\nНайдено пиктограмм: {len(pictograms)}")
    for i, (pict_key, pict_info) in enumerate(pictograms.items()):
        if i < 20:
            print(f"  - {pict_info['name']}")
    if len(pictograms) > 20:
        print(f"  ... и еще {len(pictograms) - 20}")
