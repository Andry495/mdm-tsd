"""
Тестовая версия скрипта для обработки этикеток.
Обрабатывает только первые несколько файлов для проверки.
"""

import os
import re
import pandas as pd
from pathlib import Path
from PIL import Image, ImageEnhance
import cv2
import numpy as np
from typing import List, Dict, Tuple, Optional
import pytesseract
from collections import defaultdict

# Настройка пути к Tesseract OCR (для Windows)
TESSERACT_FOUND = False
if os.name == 'nt':
    possible_paths = [
        r'C:\Program Files\Tesseract-OCR\tesseract.exe',
        r'C:\Program Files (x86)\Tesseract-OCR\tesseract.exe',
        r'C:\Users\{}\AppData\Local\Programs\Tesseract-OCR\tesseract.exe'.format(os.getenv('USERNAME', '')),
    ]
    for path in possible_paths:
        if os.path.exists(path):
            pytesseract.pytesseract.tesseract_cmd = path
            print(f"Найден Tesseract: {path}")
            TESSERACT_FOUND = True
            break
    
    if not TESSERACT_FOUND:
        print("ВНИМАНИЕ: Tesseract OCR не найден!")
        print("Скрипт будет работать без OCR (только анализ изображений)")
        print("Для установки: https://github.com/UB-Mannheim/tesseract/wiki")

# Настройка путей к каталогам
CATALOGS = [
    r"\\fileserver\public\_Дизайнеры\ЯЯЯПроцессы\Design_Work\Маркировочные",
    r"\\fileserver\public\_Маркировочные_этикетки"
]

# Ограничение для теста - обработать только первые N артикулов
MAX_ARTICLES_TO_PROCESS = 3
MAX_FILES_PER_ARTICLE = 1  # Только первый файл для каждого артикула

# Регулярное выражение для извлечения артикула
ARTICLE_PATTERN = re.compile(r'\d{4,}')

# Список пиктограмм для поиска
PICTOGRAMS = {
    'recycling': 'Переработка',
    'warning': 'Предупреждение',
    'flame': 'Огнеопасно',
    'corrosive': 'Едкое вещество',
    'explosive': 'Взрывоопасно',
    'toxic': 'Токсично',
    'environment': 'Опасность для окружающей среды',
    'glass': 'Стекло',
    'plastic': 'Пластик',
    'paper': 'Бумага',
    'metal': 'Металл',
    'compostable': 'Компостируемый',
    'biodegradable': 'Биоразлагаемый',
}

def extract_article_from_filename(filename: str) -> str:
    """Извлекает артикул (цифры) из имени файла."""
    name_without_ext = os.path.splitext(filename)[0]
    matches = ARTICLE_PATTERN.findall(name_without_ext)
    if matches:
        return max(matches, key=len)
    all_digits = re.findall(r'\d+', name_without_ext)
    if all_digits:
        return max(all_digits, key=len)
    return ""

def scan_catalogs() -> Dict[str, List[str]]:
    """Сканирует каталоги и собирает файлы по артикулам."""
    articles_files = defaultdict(list)
    
    for catalog_path in CATALOGS:
        if not os.path.exists(catalog_path):
            print(f"Предупреждение: каталог не найден: {catalog_path}")
            continue
        
        print(f"Сканирование каталога: {catalog_path}")
        
        try:
            for root, dirs, files in os.walk(catalog_path):
                for file in files:
                    if file.lower().endswith(('.jpg', '.jpeg')):
                        article = extract_article_from_filename(file)
                        if article:
                            full_path = os.path.join(root, file)
                            articles_files[article].append(full_path)
        except Exception as e:
            print(f"Ошибка при сканировании {catalog_path}: {e}")
    
    return articles_files

def preprocess_image_for_ocr(img: np.ndarray) -> np.ndarray:
    """Предобработка изображения для улучшения OCR."""
    if len(img.shape) == 3:
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    else:
        gray = img.copy()
    
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    enhanced = clahe.apply(gray)
    denoised = cv2.fastNlMeansDenoising(enhanced, None, 10, 7, 21)
    _, binary = cv2.threshold(denoised, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    
    return binary

def detect_pictograms_by_text(text: str) -> List[str]:
    """Определяет пиктограммы по текстовым признакам в OCR тексте."""
    detected = []
    text_lower = text.lower()
    
    keywords = {
        'recycling': ['переработ', 'recycl', '♻', 'recycle'],
        'warning': ['предупрежд', 'warning', 'осторожно', '⚠'],
        'flame': ['огнеопас', 'flammable', 'горюч', '🔥'],
        'corrosive': ['едк', 'corrosive', 'разъеда', '☣'],
        'explosive': ['взрывоопас', 'explosive', 'взрыв'],
        'toxic': ['токсич', 'toxic', 'ядовит', 'яд'],
        'environment': ['окружающ', 'environment', 'экологи'],
        'glass': ['стекло', 'glass'],
        'plastic': ['пластик', 'plastic', 'пластмасс'],
        'paper': ['бумаг', 'paper', 'картон'],
        'metal': ['металл', 'metal', 'железо'],
        'compostable': ['компост', 'compost'],
        'biodegradable': ['биоразлаг', 'biodegradable'],
    }
    
    for pictogram_name, keyword_list in keywords.items():
        if any(keyword in text_lower for keyword in keyword_list):
            detected.append(pictogram_name)
    
    return detected

def detect_pictograms_by_shape(img: np.ndarray) -> List[str]:
    """Определяет пиктограммы по геометрическим признакам."""
    detected = []
    
    try:
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if len(img.shape) == 3 else img
        
        edges = cv2.Canny(gray, 50, 150)
        contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        for contour in contours:
            approx = cv2.approxPolyDP(contour, 0.02 * cv2.arcLength(contour, True), True)
            if len(approx) == 3:
                if 'warning' not in detected:
                    detected.append('warning')
        
        circles = cv2.HoughCircles(gray, cv2.HOUGH_GRADIENT, 1, 20,
                                   param1=50, param2=30, minRadius=15, maxRadius=200)
        
        if circles is not None:
            num_circles = len(circles[0])
            if num_circles >= 3:
                if 'recycling' not in detected:
                    detected.append('recycling')
            elif num_circles >= 1:
                if 'warning' not in detected:
                    detected.append('warning')
        
    except Exception as e:
        print(f"Ошибка при анализе формы: {e}")
    
    return detected

def detect_pictograms(image_path: str) -> List[str]:
    """Определяет пиктограммы на изображении."""
    detected = []
    
    try:
        print(f"  Анализ пиктограмм: {os.path.basename(image_path)}")
        
        # Проверяем существование файла
        if not os.path.exists(image_path):
            print(f"  Ошибка: файл не существует: {image_path}")
            return detected
        
        # Пробуем загрузить через PIL сначала
        try:
            pil_img = Image.open(image_path)
            img_array = np.array(pil_img)
            if len(img_array.shape) == 3:
                img = cv2.cvtColor(img_array, cv2.COLOR_RGB2BGR)
            else:
                img = img_array
        except Exception as e:
            print(f"  Ошибка загрузки через PIL: {e}")
            # Пробуем через OpenCV
            img = cv2.imread(image_path)
            if img is None:
                print(f"  Ошибка: не удалось загрузить изображение через OpenCV")
                return detected
        
        processed_img = preprocess_image_for_ocr(img)
        
        if TESSERACT_FOUND:
            try:
                text = pytesseract.image_to_string(processed_img, lang='rus+eng', config='--psm 6')
            except:
                try:
                    text = pytesseract.image_to_string(processed_img, config='--psm 6')
                except Exception as e:
                    print(f"  Ошибка OCR: {e}")
                    text = ""
        else:
            text = ""
        
        text_detected = detect_pictograms_by_text(text)
        detected.extend(text_detected)
        
        shape_detected = detect_pictograms_by_shape(img)
        detected.extend(shape_detected)
        
        detected = list(set(detected))
        
        if detected:
            print(f"  Найдены пиктограммы: {', '.join(detected)}")
        else:
            print(f"  Пиктограммы не найдены")
        
    except Exception as e:
        print(f"  Ошибка при обработке: {e}")
    
    return detected

def extract_text_from_label(image_path: str) -> str:
    """Извлекает текстовую информацию с этикетки с помощью OCR."""
    try:
        print(f"  Извлечение текста: {os.path.basename(image_path)}")
        
        # Проверяем существование файла
        if not os.path.exists(image_path):
            print(f"  Ошибка: файл не существует")
            return ""
        
        # Пробуем загрузить через PIL сначала
        try:
            pil_img_orig = Image.open(image_path)
            img_array = np.array(pil_img_orig)
            if len(img_array.shape) == 3:
                img = cv2.cvtColor(img_array, cv2.COLOR_RGB2BGR)
            else:
                img = img_array
        except Exception as e:
            print(f"  Ошибка загрузки через PIL: {e}")
            img = cv2.imread(image_path)
            if img is None:
                print(f"  Ошибка: не удалось загрузить изображение")
                return ""
        
        processed_img = preprocess_image_for_ocr(img)
        pil_img = Image.fromarray(processed_img)
        
        if TESSERACT_FOUND:
            try:
                text = pytesseract.image_to_string(pil_img, lang='rus+eng', config='--psm 6')
            except:
                try:
                    text = pytesseract.image_to_string(pil_img, config='--psm 6')
                except Exception as e:
                    print(f"  Ошибка OCR: {e}")
                    text = ""
        else:
            text = ""
        
        lines = [line.strip() for line in text.split('\n') if line.strip()]
        text = ' | '.join(lines)
        
        if text:
            print(f"  Извлечено символов: {len(text)}")
            # Показываем первые 100 символов
            preview = text[:100] + "..." if len(text) > 100 else text
            print(f"  Предпросмотр: {preview}")
        
        return text
    except Exception as e:
        print(f"  Ошибка при извлечении текста: {e}")
        return ""

def process_labels():
    """Основная функция обработки этикеток (тестовая версия)."""
    print("=" * 60)
    print("ТЕСТОВАЯ ОБРАБОТКА ЭТИКЕТОК")
    print(f"Обработаем максимум {MAX_ARTICLES_TO_PROCESS} артикулов")
    print("=" * 60)
    
    articles_files = scan_catalogs()
    
    if not articles_files:
        print("Не найдено файлов с артикулами!")
        return
    
    print(f"\nНайдено {len(articles_files)} уникальных артикулов")
    print(f"Обработаем первые {min(MAX_ARTICLES_TO_PROCESS, len(articles_files))} артикулов\n")
    
    results = []
    processed_count = 0
    
    for article, file_paths in list(articles_files.items())[:MAX_ARTICLES_TO_PROCESS]:
        processed_count += 1
        print(f"\n[{processed_count}/{MAX_ARTICLES_TO_PROCESS}] Обработка артикула: {article}")
        print(f"  Найдено файлов: {len(file_paths)}")
        
        # Обрабатываем только первые файлы
        files_to_process = file_paths[:MAX_FILES_PER_ARTICLE]
        
        all_pictograms = set()
        all_texts = []
        
        for file_path in files_to_process:
            print(f"  Файл: {os.path.basename(file_path)}")
            
            pictograms = detect_pictograms(file_path)
            all_pictograms.update(pictograms)
            
            text = extract_text_from_label(file_path)
            if text:
                all_texts.append(text)
        
        pictograms_str = ', '.join(sorted(all_pictograms)) if all_pictograms else "Не найдено"
        combined_text = ' | '.join(all_texts) if all_texts else "Не извлечен"
        files_list = '; '.join([os.path.basename(f) for f in files_to_process])
        
        results.append({
            'Артикул': article,
            'Количество_файлов': len(file_paths),
            'Файлы': files_list,
            'Первый_файл': os.path.basename(file_paths[0]),
            'Путь_к_первому_файлу': file_paths[0],
            'Пиктограммы': pictograms_str,
            'Текст': combined_text,
            **{f'Пиктограмма_{name}': 'Да' if name in all_pictograms else 'Нет' 
               for name in PICTOGRAMS.keys()}
        })
    
    if results:
        df = pd.DataFrame(results)
        output_file = 'этикетки_тест.xlsx'
        df.to_excel(output_file, index=False, engine='openpyxl')
        
        print("\n" + "=" * 60)
        print(f"Результаты сохранены в файл: {output_file}")
        print(f"Обработано артикулов: {len(results)}")
        print("=" * 60)
    else:
        print("\nНет данных для сохранения")

if __name__ == "__main__":
    process_labels()
