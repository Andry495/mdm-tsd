"""Тест извлечения элементов"""

import os
import cv2
import numpy as np
from PIL import Image

# Проверяем создание каталогов
OUTPUT_DIR = "извлеченные_элементы"
PICTOGRAMS_OUTPUT_DIR = os.path.join(OUTPUT_DIR, "пиктограммы")
BRANDS_OUTPUT_DIR = os.path.join(OUTPUT_DIR, "бренды")

os.makedirs(PICTOGRAMS_OUTPUT_DIR, exist_ok=True)
os.makedirs(BRANDS_OUTPUT_DIR, exist_ok=True)

print(f"Каталоги созданы:")
print(f"  {PICTOGRAMS_OUTPUT_DIR}: {os.path.exists(PICTOGRAMS_OUTPUT_DIR)}")
print(f"  {BRANDS_OUTPUT_DIR}: {os.path.exists(BRANDS_OUTPUT_DIR)}")

# Тестируем сохранение
test_dir = os.path.join(PICTOGRAMS_OUTPUT_DIR, "Test")
os.makedirs(test_dir, exist_ok=True)

# Создаем тестовое изображение
test_img = np.zeros((100, 100, 3), dtype=np.uint8)
test_img[:] = (255, 0, 0)  # Синий цвет

test_path = os.path.join(test_dir, "test.jpg")
result = cv2.imwrite(test_path, test_img)

print(f"\nТест сохранения:")
print(f"  Путь: {test_path}")
print(f"  Результат: {result}")
print(f"  Файл существует: {os.path.exists(test_path)}")

if os.path.exists(test_path):
    print("  Успешно! Сохранение работает.")
    os.remove(test_path)  # Удаляем тестовый файл
else:
    print("  Ошибка! Сохранение не работает.")
