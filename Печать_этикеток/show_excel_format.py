"""Показать формат Excel файла"""

import pandas as pd
import os
from pathlib import Path

# Ищем Excel файлы
excel_files = list(Path('.').glob('*.xlsx'))

if not excel_files:
    print("Excel файлы не найдены в текущей директории")
    print(f"Текущая директория: {os.getcwd()}")
    print("\nСписок файлов в директории:")
    for f in Path('.').iterdir():
        if f.is_file():
            print(f"  {f.name}")
else:
    for excel_file in excel_files:
        print("=" * 80)
        print(f"Файл: {excel_file.name}")
        print("=" * 80)
        
        try:
            df = pd.read_excel(excel_file, engine='openpyxl')
            
            print(f"\nРазмер: {len(df)} строк, {len(df.columns)} колонок")
            print(f"\nКолонки ({len(df.columns)}):")
            for i, col in enumerate(df.columns, 1):
                print(f"  {i}. {col}")
            
            print(f"\n\nПервые строки данных:")
            print("-" * 80)
            # Показываем первые строки с ограничением ширины
            pd.set_option('display.max_columns', None)
            pd.set_option('display.width', None)
            pd.set_option('display.max_colwidth', 50)
            print(df.head().to_string())
            
            print(f"\n\nТипы данных:")
            print(df.dtypes)
            
            print(f"\n\nПример данных для первого артикула:")
            print("-" * 80)
            if len(df) > 0:
                first_row = df.iloc[0]
                for col in df.columns:
                    value = first_row[col]
                    if isinstance(value, str) and len(value) > 100:
                        print(f"{col}: {value[:100]}...")
                    else:
                        print(f"{col}: {value}")
            
        except Exception as e:
            print(f"Ошибка при чтении файла: {e}")
