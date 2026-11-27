"""Показать пример Excel файла"""

import pandas as pd
from openpyxl import load_workbook

try:
    # Читаем Excel файл
    df = pd.read_excel('этикетки_тест.xlsx', engine='openpyxl')
    
    print("=" * 100)
    print("СТРУКТУРА EXCEL ФАЙЛА: этикетки_тест.xlsx")
    print("=" * 100)
    
    print(f"\nВсего строк: {len(df)}")
    print(f"Всего колонок: {len(df.columns)}")
    
    print("\n" + "=" * 100)
    print("СПИСОК КОЛОНОК:")
    print("=" * 100)
    for i, col in enumerate(df.columns, 1):
        print(f"{i:2d}. {col}")
    
    print("\n" + "=" * 100)
    print("ПРИМЕР ДАННЫХ (первые 3 строки):")
    print("=" * 100)
    
    # Показываем данные построчно
    for idx, row in df.iterrows():
        print(f"\n--- СТРОКА {idx + 1} ---")
        print(f"Артикул: {row['Артикул']}")
        print(f"Количество файлов: {row['Количество_файлов']}")
        print(f"Первый файл: {row['Первый_файл']}")
        print(f"Все файлы: {row['Файлы'][:100]}..." if len(str(row['Файлы'])) > 100 else f"Все файлы: {row['Файлы']}")
        print(f"Путь: {row['Путь_к_первому_файлу'][:80]}...")
        print(f"Найденные пиктограммы: {row['Пиктограммы']}")
        print(f"Текст: {row['Текст'][:100]}..." if len(str(row['Текст'])) > 100 else f"Текст: {row['Текст']}")
        
        # Показываем только те пиктограммы, которые найдены
        print("\nДетализация пиктограмм:")
        pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
        found_pictograms = []
        for col in pictogram_cols:
            if row[col] == 'Да':
                found_pictograms.append(col.replace('Пиктограмма_', ''))
        if found_pictograms:
            print(f"  Найдено: {', '.join(found_pictograms)}")
        else:
            print("  Не найдено")
    
    print("\n" + "=" * 100)
    print("СТАТИСТИКА:")
    print("=" * 100)
    
    # Статистика по пиктограммам
    pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
    print("\nНайденные пиктограммы:")
    for col in pictogram_cols:
        count = (df[col] == 'Да').sum()
        if count > 0:
            pictogram_name = col.replace('Пиктограмма_', '')
            print(f"  {pictogram_name}: {count} артикул(ов)")
    
    print(f"\nФайл сохранен: этикетки_тест.xlsx")
    print(f"Можно открыть в Excel для просмотра и редактирования")
    
except FileNotFoundError:
    print("Файл этикетки_тест.xlsx не найден!")
    print("Запустите сначала: python test_labels.py")
except Exception as e:
    print(f"Ошибка: {e}")
    import traceback
    traceback.print_exc()
