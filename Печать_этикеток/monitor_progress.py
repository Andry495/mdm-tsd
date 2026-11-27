"""Мониторинг прогресса обработки"""

import os
import time
import pandas as pd
from datetime import datetime

output_file = 'этикетки_с_извлечением.xlsx'
check_interval = 30  # Проверка каждые 30 секунд
max_checks = 20  # Максимум проверок

print("=" * 80)
print("МОНИТОРИНГ ПРОГРЕССА ОБРАБОТКИ")
print("=" * 80)
print(f"Проверка каждые {check_interval} секунд")
print("Нажмите Ctrl+C для остановки\n")

last_count = 0
for i in range(max_checks):
    try:
        if os.path.exists(output_file):
            try:
                df = pd.read_excel(output_file, engine='openpyxl')
                current_count = len(df)
                new_records = current_count - last_count
                
                print(f"[{datetime.now().strftime('%H:%M:%S')}] Обработано: {current_count} строк", end="")
                if last_count > 0:
                    print(f" (+{new_records} за последние {check_interval} сек)", end="")
                print()
                
                if current_count > 0:
                    last_article = df.iloc[-1]['Артикул']
                    print(f"  Последний артикул: {last_article}")
                
                last_count = current_count
            except Exception as e:
                print(f"  Ошибка чтения: {e}")
        else:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] Файл еще не создан...")
        
        if i < max_checks - 1:
            time.sleep(check_interval)
    except KeyboardInterrupt:
        print("\n\nМониторинг остановлен пользователем")
        break

print("\n" + "=" * 80)
if os.path.exists(output_file):
    try:
        df = pd.read_excel(output_file, engine='openpyxl')
        print(f"ИТОГОВЫЙ РЕЗУЛЬТАТ:")
        print(f"  Обработано строк: {len(df)}")
        print(f"  Размер файла: {os.path.getsize(output_file) / 1024 / 1024:.2f} МБ")
    except:
        pass
