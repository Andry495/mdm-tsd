"""Отладка загрузки брендов"""

import os

SOURCE_DIR = r"\\bytplast.local\public\_Проекты\Честный_знак\02_Исходные_материалы\Печать_этикеток"

print("Проверка каталога:")
print(f"Существует: {os.path.exists(SOURCE_DIR)}")

if os.path.exists(SOURCE_DIR):
    print("\nСодержимое:")
    for item in os.listdir(SOURCE_DIR):
        item_path = os.path.join(SOURCE_DIR, item)
        if os.path.isdir(item_path):
            print(f"\nПапка: {item}")
            try:
                files = os.listdir(item_path)
                print(f"  Файлов: {len(files)}")
                for f in files[:10]:
                    print(f"    - {f}")
            except Exception as e:
                print(f"  Ошибка: {e}")
