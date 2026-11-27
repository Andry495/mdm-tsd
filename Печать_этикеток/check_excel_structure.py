"""Проверка структуры Excel файла"""

import pandas as pd

try:
    df = pd.read_excel('этикетки_с_извлечением.xlsx', engine='openpyxl')
    
    print("=" * 100)
    print("СТРУКТУРА EXCEL ФАЙЛА")
    print("=" * 100)
    
    print(f"\nВсего строк: {len(df)}")
    print(f"Всего колонок: {len(df.columns)}")
    
    print("\n" + "=" * 100)
    print("СПИСОК КОЛОНОК:")
    print("=" * 100)
    for i, col in enumerate(df.columns, 1):
        print(f"{i:2d}. {col}")
    
    print("\n" + "=" * 100)
    print("ПРИМЕР ДАННЫХ (первая строка):")
    print("=" * 100)
    
    if len(df) > 0:
        row = df.iloc[0]
        print(f"\nАртикул: {row['Артикул']}")
        if 'Дата_файла' in row:
            print(f"Дата файла: {row['Дата_файла']}")
        if 'Дата_файла_только_дата' in row:
            print(f"Дата файла (только дата): {row['Дата_файла_только_дата']}")
        print(f"Бренд: {row['Бренд']}")
        print(f"Пиктограммы найдены: {row['Пиктограммы_найдены']}")
        print(f"Текстовые блоки: {str(row['Текстовые_блоки'])[:200]}...")
        
        # Показываем пиктограммы
        print("\nПиктограммы (Да/Нет):")
        pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
        found_pictograms = []
        for col in pictogram_cols[:10]:  # Первые 10
            if row[col] == 'Да':
                found_pictograms.append(col.replace('Пиктограмма_', ''))
        if found_pictograms:
            print(f"  Найдено: {', '.join(found_pictograms)}")
        else:
            print("  Не найдено")
        if len(pictogram_cols) > 10:
            print(f"  ... и еще {len(pictogram_cols) - 10} колонок с пиктограммами")
    
    print("\n" + "=" * 100)
    print("СТАТИСТИКА:")
    print("=" * 100)
    
    # Статистика по пиктограммам
    pictogram_cols = [col for col in df.columns if col.startswith('Пиктограмма_')]
    print(f"\nВсего колонок с пиктограммами: {len(pictogram_cols)}")
    
    print("\nНайденные пиктограммы (по всем артикулам):")
    for col in pictogram_cols:
        count = (df[col] == 'Да').sum()
        if count > 0:
            pictogram_name = col.replace('Пиктограмма_', '')
            print(f"  {pictogram_name}: найдено в {count} артикулах")
    
except FileNotFoundError:
    print("Файл этикетки_с_извлечением.xlsx не найден!")
except Exception as e:
    print(f"Ошибка: {e}")
    import traceback
    traceback.print_exc()
