"""Просмотр результатов обработки этикеток"""

import pandas as pd

try:
    df = pd.read_excel('этикетки_тест.xlsx', engine='openpyxl')
    
    print("=" * 80)
    print("РЕЗУЛЬТАТЫ ОБРАБОТКИ ЭТИКЕТОК")
    print("=" * 80)
    print(f"\nВсего обработано артикулов: {len(df)}")
    print("\n" + "-" * 80)
    
    for idx, row in df.iterrows():
        print(f"\nАртикул: {row['Артикул']}")
        print(f"  Файл: {row['Первый_файл']}")
        print(f"  Всего файлов: {row['Количество_файлов']}")
        print(f"  Найденные пиктограммы: {row['Пиктограммы']}")
        print(f"  Текст: {row['Текст'][:200] if len(str(row['Текст'])) > 200 else row['Текст']}")
        print(f"  Путь: {row['Путь_к_первому_файлу']}")
        print("-" * 80)
    
    print("\n\nДетализация по пиктограммам:")
    pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
    for col in pictogram_cols:
        count = (df[col] == 'Да').sum()
        if count > 0:
            print(f"  {col.replace('Пиктограмма_', '')}: найдено в {count} артикулах")
    
except FileNotFoundError:
    print("Файл этикетки_тест.xlsx не найден!")
except Exception as e:
    print(f"Ошибка при чтении файла: {e}")
