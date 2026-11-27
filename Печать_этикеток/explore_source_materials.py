"""Изучение каталога с исходными материалами"""

import os
from pathlib import Path

SOURCE_DIR = r"\\fileserver\public\_Проекты\Честный_знак\02_Исходные_материалы\Печать_этикеток"

print("=" * 80)
print("ИЗУЧЕНИЕ КАТАЛОГА С ИСХОДНЫМИ МАТЕРИАЛАМИ")
print("=" * 80)
print(f"\nПуть: {SOURCE_DIR}")
print(f"Существует: {os.path.exists(SOURCE_DIR)}")

if os.path.exists(SOURCE_DIR):
    print("\nСодержимое каталога:")
    print("-" * 80)
    
    items = []
    try:
        for item in os.listdir(SOURCE_DIR):
            item_path = os.path.join(SOURCE_DIR, item)
            items.append((item, os.path.isdir(item_path), os.path.isfile(item_path)))
    except Exception as e:
        print(f"Ошибка при чтении каталога: {e}")
        items = []
    
    # Показываем папки
    dirs = [item for item, is_dir, is_file in items if is_dir]
    files = [item for item, is_dir, is_file in items if is_file]
    
    print(f"\nПапки ({len(dirs)}):")
    for d in dirs[:20]:
        print(f"  [DIR] {d}")
    
    print(f"\nФайлы ({len(files)}):")
    for f in files[:30]:
        print(f"  [FILE] {f}")
    
    # Ищем Excel файлы, текстовые файлы, изображения
    excel_files = [f for f in files if f.lower().endswith(('.xlsx', '.xls'))]
    txt_files = [f for f in files if f.lower().endswith(('.txt', '.csv'))]
    img_files = [f for f in files if f.lower().endswith(('.jpg', '.jpeg', '.png'))]
    
    print(f"\n\nНайдено:")
    print(f"  Excel файлов: {len(excel_files)}")
    if excel_files:
        for f in excel_files[:10]:
            print(f"    - {f}")
    
    print(f"  Текстовых файлов: {len(txt_files)}")
    if txt_files:
        for f in txt_files[:10]:
            print(f"    - {f}")
    
    print(f"  Изображений: {len(img_files)}")
    
    # Изучаем подкаталоги
    print(f"\n\nИзучение подкаталогов:")
    print("-" * 80)
    for d in dirs[:10]:
        subdir_path = os.path.join(SOURCE_DIR, d)
        try:
            sub_items = os.listdir(subdir_path)
            print(f"\n[DIR] {d} ({len(sub_items)} элементов):")
            for sub_item in sub_items[:10]:
                print(f"    - {sub_item}")
        except Exception as e:
            print(f"\n[DIR] {d} - ошибка доступа: {e}")
    
    # Пробуем найти файлы с информацией о брендах
    print(f"\n\nПоиск файлов с информацией о брендах:")
    print("-" * 80)
    brand_keywords = ['бренд', 'brand', 'марка', 'logo', 'пиктограмм', 'pictogram']
    
    all_files_to_check = []
    for root, dirs_list, files_list in os.walk(SOURCE_DIR):
        for file in files_list:
            file_lower = file.lower()
            if any(keyword in file_lower for keyword in brand_keywords):
                all_files_to_check.append(os.path.join(root, file))
    
    if all_files_to_check:
        print(f"Найдено {len(all_files_to_check)} потенциально релевантных файлов:")
        for f in all_files_to_check[:20]:
            print(f"  - {f}")
    else:
        print("Файлы с ключевыми словами не найдены")
        
else:
    print("\nКаталог не найден или недоступен!")
    print("Проверьте доступ к сетевому диску")
