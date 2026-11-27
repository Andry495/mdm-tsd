"""Показать результаты улучшенной обработки"""

import pandas as pd

try:
    df = pd.read_excel('этикетки_улучшенный.xlsx', engine='openpyxl')
    
    print("=" * 100)
    print("РЕЗУЛЬТАТЫ УЛУЧШЕННОЙ ОБРАБОТКИ")
    print("=" * 100)
    
    print(f"\nВсего строк: {len(df)}")
    print(f"Всего колонок: {len(df.columns)}")
    
    print("\n" + "=" * 100)
    print("СПИСОК КОЛОНОК:")
    print("=" * 100)
    for i, col in enumerate(df.columns, 1):
        print(f"{i:2d}. {col}")
    
    print("\n" + "=" * 100)
    print("ПРИМЕР ДАННЫХ:")
    print("=" * 100)
    
    for idx, row in df.iterrows():
        print(f"\n--- СТРОКА {idx + 1} ---")
        print(f"Артикул: {row['Артикул']}")
        print(f"Бренд: {row['Бренд']}")
        print(f"Пиктограмма бренда: {row['Пиктограмма_бренда']}")
        print(f"Найденные пиктограммы: {row['Пиктограммы']}")
        print(f"Текст полный: {str(row['Текст_полный'])[:150]}...")
        print(f"Текстовые блоки: {str(row['Текстовые_блоки'])[:200]}...")
        
except FileNotFoundError:
    print("Файл этикетки_улучшенный.xlsx не найден!")
except Exception as e:
    print(f"Ошибка: {e}")
    import traceback
    traceback.print_exc()
