"""Проверка прогресса обработки"""

import os
import pandas as pd
from datetime import datetime

output_file = 'этикетки_с_извлечением.xlsx'

print("=" * 80)
print("ПРОВЕРКА ПРОГРЕССА ОБРАБОТКИ")
print("=" * 80)
print(f"Время проверки: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")

if os.path.exists(output_file):
    try:
        df = pd.read_excel(output_file, engine='openpyxl')
        print(f"\nФайл существует: {output_file}")
        print(f"Размер файла: {os.path.getsize(output_file) / 1024 / 1024:.2f} МБ")
        print(f"Обработано строк: {len(df)}")
        
        if len(df) > 0:
            print(f"\nПоследний обработанный артикул: {df.iloc[-1]['Артикул']}")
            print(f"Дата последнего файла: {df.iloc[-1].get('Дата_файла', 'N/A')}")
            
            # Статистика
            print(f"\nСтатистика:")
            print(f"  Уникальных артикулов: {df['Артикул'].nunique()}")
            print(f"  Найдено брендов: {df[df['Бренд'] != 'Не определен']['Бренд'].nunique()}")
            
            # Пиктограммы
            pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
            found_pictograms = []
            for col in pictogram_cols:
                count = (df[col] == 'Да').sum()
                if count > 0:
                    found_pictograms.append((col.replace('Пиктограмма_', ''), count))
            
            if found_pictograms:
                print(f"  Найдено пиктограмм: {len(found_pictograms)}")
                print(f"  Топ-5 пиктограмм:")
                for name, count in sorted(found_pictograms, key=lambda x: x[1], reverse=True)[:5]:
                    print(f"    - {name}: {count} раз")
        
    except Exception as e:
        print(f"\nОшибка при чтении файла: {e}")
else:
    print(f"\nФайл {output_file} еще не создан")
    print("Обработка еще не началась или находится на начальной стадии")

# Проверяем каталоги с извлеченными элементами
output_dir = "извлеченные_элементы"
if os.path.exists(output_dir):
    pictograms_dir = os.path.join(output_dir, "пиктограммы")
    brands_dir = os.path.join(output_dir, "бренды")
    
    print(f"\n\nИзвлеченные элементы:")
    if os.path.exists(pictograms_dir):
        pict_dirs = [d for d in os.listdir(pictograms_dir) if os.path.isdir(os.path.join(pictograms_dir, d))]
        total_pict_files = 0
        for d in pict_dirs:
            files = [f for f in os.listdir(os.path.join(pictograms_dir, d)) if f.endswith('.jpg')]
            total_pict_files += len(files)
        print(f"  Пиктограммы: {len(pict_dirs)} каталогов, {total_pict_files} файлов")
    
    if os.path.exists(brands_dir):
        brand_dirs = [d for d in os.listdir(brands_dir) if os.path.isdir(os.path.join(brands_dir, d))]
        total_brand_files = 0
        for d in brand_dirs:
            files = [f for f in os.listdir(os.path.join(brands_dir, d)) if f.endswith('.jpg')]
            total_brand_files += len(files)
        print(f"  Бренды: {len(brand_dirs)} каталогов, {total_brand_files} файлов")

print("\n" + "=" * 80)
