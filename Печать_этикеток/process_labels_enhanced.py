"""
Улучшенный скрипт для обработки этикеток.
Определяет бренды, пиктограммы брендов и выделяет текстовые блоки.
"""

import os
import re
import pandas as pd
from pathlib import Path
from PIL import Image
import cv2
import numpy as np
from typing import List, Dict, Tuple, Optional
import pytesseract
from collections import defaultdict

# Настройка пути к Tesseract OCR
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
            TESSERACT_FOUND = True
            print(f"Найден Tesseract: {path}")
            break
    
    if not TESSERACT_FOUND:
        print("ВНИМАНИЕ: Tesseract OCR не найден! Текст не будет извлекаться.")

# Настройка путей
CATALOGS = [
    r"\\fileserver\public\_Дизайнеры\ЯЯЯПроцессы\Design_Work\Маркировочные",
    r"\\fileserver\public\_Маркировочные_этикетки"
]

SOURCE_DIR = r"\\fileserver\public\_Проекты\Честный_знак\02_Исходные_материалы\Печать_этикеток"

# Находим папки с брендами и пиктограммами
BRANDS_DIR = None
PICTOGRAMS_DIR = None

if os.path.exists(SOURCE_DIR):
    for item in os.listdir(SOURCE_DIR):
        item_path = os.path.join(SOURCE_DIR, item)
        if os.path.isdir(item_path):
            item_lower = item.lower()
            if 'бренд' in item_lower or 'brand' in item_lower:
                BRANDS_DIR = item_path
            elif 'пиктограмм' in item_lower or 'pictogram' in item_lower:
                PICTOGRAMS_DIR = item_path

# Регулярное выражение для извлечения артикула
ARTICLE_PATTERN = re.compile(r'\d{4,}')

# Ограничение для теста
MAX_ARTICLES_TO_PROCESS = 3
MAX_FILES_PER_ARTICLE = 1

# Загрузка брендов и пиктограмм
def load_brands() -> Dict[str, Dict]:
    """Загружает список брендов из каталога"""
    brands = {}
    
    # Находим папку с брендами
    brands_path = None
    if BRANDS_DIR and os.path.exists(BRANDS_DIR):
        brands_path = BRANDS_DIR
    elif os.path.exists(SOURCE_DIR):
        for item in os.listdir(SOURCE_DIR):
            item_path = os.path.join(SOURCE_DIR, item)
            if os.path.isdir(item_path) and ('бренд' in item.lower() or 'brand' in item.lower()):
                brands_path = item_path
                break
    
    if not brands_path:
        return brands
    
    try:
        for file in os.listdir(brands_path):
            if file.lower().endswith(('.png', '.jpg', '.jpeg', '.svg')):
                brand_name = os.path.splitext(file)[0]
                brand_key = brand_name.lower().replace(' ', '_').replace('-', '_')
                brands[brand_key] = {
                    'name': brand_name,
                    'filename': file,
                    'keywords': [brand_name.lower(), brand_key]
                }
                # Добавляем варианты написания
                if ' ' in brand_name:
                    brands[brand_key]['keywords'].extend(brand_name.lower().split())
                if '-' in brand_name:
                    brands[brand_key]['keywords'].extend(brand_name.lower().split('-'))
    except Exception as e:
        print(f"Ошибка при загрузке брендов: {e}")
    
    return brands

def load_pictograms() -> Dict[str, Dict]:
    """Загружает список пиктограмм из каталога"""
    pictograms = {}
    
    # Находим папку с пиктограммами
    pictograms_path = None
    if PICTOGRAMS_DIR and os.path.exists(PICTOGRAMS_DIR):
        pictograms_path = PICTOGRAMS_DIR
    elif os.path.exists(SOURCE_DIR):
        for item in os.listdir(SOURCE_DIR):
            item_path = os.path.join(SOURCE_DIR, item)
            if os.path.isdir(item_path) and ('пиктограмм' in item.lower() or 'pictogram' in item.lower()):
                pictograms_path = item_path
                break
    
    if not pictograms_path:
        return pictograms
    
    try:
        for file in os.listdir(pictograms_path):
            if file.lower().endswith(('.png', '.jpg', '.jpeg', '.svg')):
                pictogram_name = os.path.splitext(file)[0]
                pictogram_key = pictogram_name.lower().replace(' ', '_').replace('-', '_')
                pictograms[pictogram_key] = {
                    'name': pictogram_name,
                    'filename': file
                }
    except Exception as e:
        print(f"Ошибка при загрузке пиктограмм: {e}")
    
    return pictograms

# Загружаем бренды и пиктограммы
BRANDS = load_brands()
PICTOGRAMS_DB = load_pictograms()

print(f"Загружено брендов: {len(BRANDS)}")
print(f"Загружено пиктограмм: {len(PICTOGRAMS_DB)}")

def extract_article_from_filename(filename: str) -> str:
    """Извлекает артикул из имени файла."""
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

def extract_text_blocks(image_path: str) -> List[Dict]:
    """Извлекает текстовые блоки с координатами и текстом."""
    text_blocks = []
    
    try:
        if not os.path.exists(image_path):
            return text_blocks
        
        # Загружаем изображение
        try:
            pil_img = Image.open(image_path)
            img_array = np.array(pil_img)
            if len(img_array.shape) == 3:
                img = cv2.cvtColor(img_array, cv2.COLOR_RGB2BGR)
            else:
                img = img_array
        except:
            img = cv2.imread(image_path)
            if img is None:
                return text_blocks
        
        processed_img = preprocess_image_for_ocr(img)
        
        if TESSERACT_FOUND:
            try:
                # Получаем данные с координатами
                data = pytesseract.image_to_data(processed_img, lang='rus+eng', output_type=pytesseract.Output.DICT)
                
                # Группируем слова в блоки
                current_block = []
                current_y = None
                
                for i in range(len(data['text'])):
                    text = data['text'][i].strip()
                    if text:
                        y = data['top'][i]
                        if current_y is None or abs(y - current_y) < 10:  # Один блок
                            current_block.append({
                                'text': text,
                                'x': data['left'][i],
                                'y': data['top'][i],
                                'width': data['width'][i],
                                'height': data['height'][i],
                                'conf': data['conf'][i]
                            })
                            current_y = y
                        else:
                            # Новый блок
                            if current_block:
                                block_text = ' '.join([w['text'] for w in current_block])
                                text_blocks.append({
                                    'text': block_text,
                                    'y_position': current_block[0]['y']
                                })
                            current_block = [{
                                'text': text,
                                'x': data['left'][i],
                                'y': data['top'][i],
                                'width': data['width'][i],
                                'height': data['height'][i],
                                'conf': data['conf'][i]
                            }]
                            current_y = y
                
                # Добавляем последний блок
                if current_block:
                    block_text = ' '.join([w['text'] for w in current_block])
                    text_blocks.append({
                        'text': block_text,
                        'y_position': current_block[0]['y']
                    })
                
            except Exception as e:
                print(f"  Ошибка при извлечении блоков: {e}")
        
    except Exception as e:
        print(f"  Ошибка: {e}")
    
    return text_blocks

def detect_brand(text: str, image_path: str) -> Tuple[str, str]:
    """Определяет бренд по тексту и пути к файлу."""
    text_lower = text.lower()
    
    # Проверяем по тексту
    for brand_key, brand_info in BRANDS.items():
        for keyword in brand_info['keywords']:
            if keyword in text_lower:
                return brand_info['name'], brand_key
    
    # Проверяем по пути к файлу
    path_lower = image_path.lower()
    for brand_key, brand_info in BRANDS.items():
        for keyword in brand_info['keywords']:
            if keyword in path_lower:
                return brand_info['name'], brand_key
    
    return "Не определен", ""

def detect_brand_pictogram(brand_key: str, text: str) -> str:
    """Определяет пиктограмму бренда."""
    if not brand_key:
        return "Не определена"
    
    text_lower = text.lower()
    
    # Ищем упоминания пиктограмм в тексте
    for pict_key, pict_info in PICTOGRAMS_DB.items():
        pict_name_lower = pict_info['name'].lower()
        # Проверяем точное совпадение или частичное
        if pict_name_lower in text_lower or any(word in text_lower for word in pict_name_lower.split() if len(word) > 2):
            return pict_info['name']
    
    return "Не определена"

def detect_pictograms(image_path: str) -> List[str]:
    """Определяет пиктограммы на изображении."""
    detected = []
    
    try:
        if not os.path.exists(image_path):
            return detected
        
        try:
            pil_img = Image.open(image_path)
            img_array = np.array(pil_img)
            if len(img_array.shape) == 3:
                img = cv2.cvtColor(img_array, cv2.COLOR_RGB2BGR)
            else:
                img = img_array
        except:
            img = cv2.imread(image_path)
            if img is None:
                return detected
        
        processed_img = preprocess_image_for_ocr(img)
        
        text = ""
        if TESSERACT_FOUND:
            try:
                text = pytesseract.image_to_string(processed_img, lang='rus+eng', config='--psm 6')
            except:
                try:
                    text = pytesseract.image_to_string(processed_img, config='--psm 6')
                except:
                    pass
        
        text_lower = text.lower()
        
        # Ищем пиктограммы из базы данных
        for pict_key, pict_info in PICTOGRAMS_DB.items():
            pict_name_lower = pict_info['name'].lower()
            if pict_name_lower in text_lower:
                detected.append(pict_info['name'])
        
        # Геометрический анализ
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if len(img.shape) == 3 else img
        edges = cv2.Canny(gray, 50, 150)
        contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        for contour in contours:
            approx = cv2.approxPolyDP(contour, 0.02 * cv2.arcLength(contour, True), True)
            if len(approx) == 3:
                if 'warning' not in [d.lower() for d in detected]:
                    detected.append('Warning')
        
        circles = cv2.HoughCircles(gray, cv2.HOUGH_GRADIENT, 1, 20,
                                   param1=50, param2=30, minRadius=15, maxRadius=200)
        
        if circles is not None and len(circles[0]) >= 3:
            if 'recycling' not in [d.lower() for d in detected]:
                detected.append('Recycling')
        
    except Exception as e:
        print(f"  Ошибка при обработке: {e}")
    
    return detected

def extract_text_from_label(image_path: str) -> str:
    """Извлекает текстовую информацию с этикетки."""
    try:
        if not os.path.exists(image_path):
            return ""
        
        try:
            pil_img_orig = Image.open(image_path)
            img_array = np.array(pil_img_orig)
            if len(img_array.shape) == 3:
                img = cv2.cvtColor(img_array, cv2.COLOR_RGB2BGR)
            else:
                img = img_array
        except:
            img = cv2.imread(image_path)
            if img is None:
                return ""
        
        processed_img = preprocess_image_for_ocr(img)
        pil_img = Image.fromarray(processed_img)
        
        text = ""
        if TESSERACT_FOUND:
            try:
                text = pytesseract.image_to_string(pil_img, lang='rus+eng', config='--psm 6')
            except:
                try:
                    text = pytesseract.image_to_string(pil_img, config='--psm 6')
                except:
                    pass
        
        lines = [line.strip() for line in text.split('\n') if line.strip()]
        return ' | '.join(lines)
    except Exception as e:
        return ""

def process_labels():
    """Основная функция обработки этикеток."""
    print("=" * 80)
    print("ОБРАБОТКА ЭТИКЕТОК (улучшенная версия)")
    print(f"Обработаем максимум {MAX_ARTICLES_TO_PROCESS} артикулов")
    print("=" * 80)
    
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
        
        files_to_process = file_paths[:MAX_FILES_PER_ARTICLE]
        
        all_pictograms = set()
        all_texts = []
        all_text_blocks = []
        detected_brand = "Не определен"
        detected_brand_key = ""
        brand_pictogram = "Не определена"
        
        for file_path in files_to_process:
            print(f"  Файл: {os.path.basename(file_path)}")
            
            # Извлекаем текст
            text = extract_text_from_label(file_path)
            if text:
                all_texts.append(text)
                print(f"  Извлечено символов: {len(text)}")
            
            # Определяем бренд
            brand_name, brand_key = detect_brand(text, file_path)
            if brand_name != "Не определен":
                detected_brand = brand_name
                detected_brand_key = brand_key
                print(f"  Найден бренд: {detected_brand}")
            
            # Определяем пиктограмму бренда
            if detected_brand_key:
                brand_pictogram = detect_brand_pictogram(detected_brand_key, text)
                if brand_pictogram != "Не определена":
                    print(f"  Пиктограмма бренда: {brand_pictogram}")
            
            # Извлекаем текстовые блоки
            text_blocks = extract_text_blocks(file_path)
            if text_blocks:
                all_text_blocks.extend(text_blocks)
                print(f"  Найдено текстовых блоков: {len(text_blocks)}")
            
            # Определяем пиктограммы
            pictograms = detect_pictograms(file_path)
            all_pictograms.update(pictograms)
            if pictograms:
                print(f"  Найдены пиктограммы: {', '.join(pictograms)}")
        
        # Формируем данные
        pictograms_str = ', '.join(sorted(all_pictograms)) if all_pictograms else "Не найдено"
        combined_text = ' | '.join(all_texts) if all_texts else "Не извлечен"
        
        # Формируем текстовые блоки
        text_blocks_str = ""
        if all_text_blocks:
            blocks_list = [f"Блок {i+1}: {block['text'][:100]}" for i, block in enumerate(all_text_blocks[:10])]
            text_blocks_str = ' || '.join(blocks_list)
            if len(all_text_blocks) > 10:
                text_blocks_str += f" ... (всего {len(all_text_blocks)} блоков)"
        
        files_list = '; '.join([os.path.basename(f) for f in files_to_process])
        
        results.append({
            'Артикул': article,
            'Количество_файлов': len(file_paths),
            'Файлы': files_list,
            'Первый_файл': os.path.basename(file_paths[0]),
            'Путь_к_первому_файлу': file_paths[0],
            'Бренд': detected_brand,
            'Пиктограмма_бренда': brand_pictogram,
            'Пиктограммы': pictograms_str,
            'Текст_полный': combined_text,
            'Текстовые_блоки': text_blocks_str,
            **{f'Пиктограмма_{pict_info["name"]}': 'Да' if pict_info['name'] in all_pictograms else 'Нет' 
               for pict_info in PICTOGRAMS_DB.values()}
        })
    
    if results:
        df = pd.DataFrame(results)
        output_file = 'этикетки_улучшенный.xlsx'
        df.to_excel(output_file, index=False, engine='openpyxl')
        
        print("\n" + "=" * 80)
        print(f"Результаты сохранены в файл: {output_file}")
        print(f"Обработано артикулов: {len(results)}")
        print("=" * 80)
    else:
        print("\nНет данных для сохранения")

if __name__ == "__main__":
    process_labels()
