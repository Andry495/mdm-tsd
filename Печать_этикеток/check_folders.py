"""Проверка папок"""

import os

SOURCE_DIR = r"\\fileserver\public\_Проекты\Честный_знак\02_Исходные_материалы\Печать_этикеток"

if os.path.exists(SOURCE_DIR):
    print("Содержимое каталога:")
    for item in os.listdir(SOURCE_DIR):
        item_path = os.path.join(SOURCE_DIR, item)
        if os.path.isdir(item_path):
            print(f"Папка: '{item}' (repr: {repr(item)})")
            # Показываем первые файлы
            try:
                files = os.listdir(item_path)
                print(f"  Файлов в папке: {len(files)}")
                for f in files[:5]:
                    print(f"    - {f}")
            except Exception as e:
                print(f"  Ошибка: {e}")
