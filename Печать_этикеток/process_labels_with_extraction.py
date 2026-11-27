"""
Улучшенный скрипт для обработки этикеток.
Определяет бренды, пиктограммы и сохраняет их в отдельные каталоги.
"""

import os
import re
import shutil
import pandas as pd
from pathlib import Path
from PIL import Image
import cv2
import numpy as np
from typing import List, Dict, Tuple, Optional
import pytesseract
from collections import defaultdict
from datetime import datetime

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

# Каталоги для сохранения извлеченных элементов
OUTPUT_DIR = "извлеченные_элементы"
PICTOGRAMS_OUTPUT_DIR = os.path.join(OUTPUT_DIR, "пиктограммы")
BRANDS_OUTPUT_DIR = os.path.join(OUTPUT_DIR, "бренды")

# Создаем структуру каталогов
os.makedirs(PICTOGRAMS_OUTPUT_DIR, exist_ok=True)
os.makedirs(BRANDS_OUTPUT_DIR, exist_ok=True)

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

# Ограничение для полной обработки (None = без ограничений)
MAX_ARTICLES_TO_PROCESS = None  # Обработать все артикулы
MAX_FILES_PER_ARTICLE = None  # Обработать все файлы для каждого артикула

# Загрузка брендов и пиктограмм
def load_brands() -> Dict[str, Dict]:
    """Загружает список брендов из каталога"""
    brands = {}
    
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

def extract_text_blocks(image_path: str) -> List[str]:
    """Извлекает текстовые блоки с этикетки."""
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
                
                # Группируем слова в блоки по Y координате
                blocks_dict = {}  # y_position -> список слов
                
                for i in range(len(data['text'])):
                    text = data['text'][i].strip()
                    if text and int(data['conf'][i]) > 0:  # Только с достаточной уверенностью
                        y = data['top'][i]
                        # Группируем по близким Y координатам (в пределах 15 пикселей)
                        found_block = False
                        for block_y in blocks_dict.keys():
                            if abs(y - block_y) < 15:
                                blocks_dict[block_y].append(text)
                                found_block = True
                                break
                        if not found_block:
                            blocks_dict[y] = [text]
                
                # Формируем текстовые блоки
                for y_pos in sorted(blocks_dict.keys()):
                    block_text = ' '.join(blocks_dict[y_pos])
                    if len(block_text.strip()) > 2:  # Минимум 3 символа
                        text_blocks.append(block_text.strip())
                
            except Exception as e:
                # Если не получилось с координатами, просто разбиваем по строкам
                try:
                    text = pytesseract.image_to_string(processed_img, lang='rus+eng', config='--psm 6')
                    lines = [line.strip() for line in text.split('\n') if line.strip() and len(line.strip()) > 2]
                    text_blocks.extend(lines)
                except:
                    pass
        else:
            # Без OCR - используем простое разбиение изображения на зоны
            h, w = img.shape[:2]
            # Делим на 4 зоны по вертикали
            zone_height = h // 4
            for i in range(4):
                zone = img[i*zone_height:(i+1)*zone_height, :]
                # Можно добавить простую обработку зон
                text_blocks.append(f"[Зона {i+1}]")
    
    except Exception as e:
        print(f"  Ошибка при извлечении блоков: {e}")
    
    return text_blocks

def extract_regions_by_contours(img: np.ndarray, min_area: int = 100) -> List[Tuple[int, int, int, int]]:
    """Извлекает регионы изображения по контурам."""
    regions = []
    
    try:
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if len(img.shape) == 3 else img
        
        # Находим контуры
        edges = cv2.Canny(gray, 50, 150)
        contours, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        for contour in contours:
            area = cv2.contourArea(contour)
            if area > min_area:
                x, y, w, h = cv2.boundingRect(contour)
                # Фильтруем слишком маленькие или слишком большие регионы
                if 20 < w < img.shape[1] * 0.8 and 20 < h < img.shape[0] * 0.8:
                    regions.append((x, y, w, h))
    except Exception as e:
        print(f"  Ошибка при извлечении регионов: {e}")
    
    return regions

def extract_circular_regions(img: np.ndarray) -> List[Tuple[int, int, int]]:
    """Извлекает круглые регионы (пиктограммы часто круглые)."""
    regions = []
    
    try:
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY) if len(img.shape) == 3 else img
        
        # Находим круги
        circles = cv2.HoughCircles(gray, cv2.HOUGH_GRADIENT, 1, 20,
                                   param1=50, param2=30, minRadius=15, maxRadius=min(img.shape[0], img.shape[1]) // 4)
        
        if circles is not None:
            circles = np.uint16(np.around(circles))
            for i in circles[0, :]:
                regions.append((i[0], i[1], i[2]))  # x, y, radius
    except Exception as e:
        pass
    
    return regions

def save_extracted_region(img: np.ndarray, region: Tuple, output_path: str, region_type: str = "rect"):
    """Сохраняет извлеченный регион."""
    try:
        if region_type == "rect":
            x, y, w, h = region
            # Проверяем границы
            x = max(0, min(x, img.shape[1] - 1))
            y = max(0, min(y, img.shape[0] - 1))
            w = min(w, img.shape[1] - x)
            h = min(h, img.shape[0] - y)
            if w > 0 and h > 0:
                roi = img[y:y+h, x:x+w]
            else:
                return False
        elif region_type == "circle":
            x, y, r = region
            # Проверяем границы
            x = int(max(r, min(x, img.shape[1] - r)))
            y = int(max(r, min(y, img.shape[0] - r)))
            r = int(min(r, min(img.shape[0]//2, img.shape[1]//2)))
            if r > 0:
                # Вырезаем квадрат вокруг круга
                roi = img[max(0, y-r):min(img.shape[0], y+r), max(0, x-r):min(img.shape[1], x+r)]
            else:
                return False
        else:
            return False
        
        if roi.size > 0 and roi.shape[0] > 10 and roi.shape[1] > 10:
            try:
                # Пробуем через OpenCV
                success = cv2.imwrite(output_path, roi)
                if success and os.path.exists(output_path):
                    return True
                # Если не получилось, пробуем через PIL
                if len(roi.shape) == 3:
                    roi_rgb = cv2.cvtColor(roi, cv2.COLOR_BGR2RGB)
                    pil_roi = Image.fromarray(roi_rgb)
                    pil_roi.save(output_path, 'JPEG', quality=95)
                    if os.path.exists(output_path):
                        return True
            except Exception as e:
                pass
    except Exception as e:
        print(f"  Ошибка при сохранении региона: {e}")
        import traceback
        traceback.print_exc()
    return False

def extract_and_save_pictograms(image_path: str, article: str, detected_pictograms: List[str]) -> List[str]:
    """Извлекает и сохраняет пиктограммы с этикетки."""
    saved_paths = []
    
    try:
        if not os.path.exists(image_path):
            return saved_paths
        
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
                return saved_paths
        
        # Извлекаем регионы
        regions = extract_regions_by_contours(img, min_area=200)
        circular_regions = extract_circular_regions(img)
        
        # Сохраняем найденные пиктограммы
        for i, pictogram_name in enumerate(detected_pictograms):
            # Создаем каталог для пиктограммы
            safe_name = pictogram_name.replace('/', '_').replace('\\', '_').replace(':', '_')
            pictogram_dir = os.path.join(PICTOGRAMS_OUTPUT_DIR, safe_name)
            os.makedirs(pictogram_dir, exist_ok=True)
            
            # Пробуем сохранить регионы
            saved_count = 0
            
            # Сначала пробуем круглые регионы (пиктограммы часто круглые)
            if circular_regions:
                for j, circle in enumerate(circular_regions[:2]):
                    output_filename = f"{article}_circle_{j}.jpg"
                    output_path = os.path.join(pictogram_dir, output_filename)
                    if save_extracted_region(img, circle, output_path, "circle"):
                        saved_paths.append(output_path)
                        saved_count += 1
                        if saved_count >= 1:
                            break
            
            # Если не нашли круглые, пробуем прямоугольные
            if saved_count == 0 and regions:
                for j, region in enumerate(regions[:3]):
                    output_filename = f"{article}_rect_{j}.jpg"
                    output_path = os.path.join(pictogram_dir, output_filename)
                    if save_extracted_region(img, region, output_path, "rect"):
                        saved_paths.append(output_path)
                        saved_count += 1
                        if saved_count >= 1:
                            break
            
            # Если все еще не нашли, сохраняем часть изображения (например, правый верхний угол)
            if saved_count == 0:
                h, w = img.shape[:2]
                # Сохраняем правый верхний угол (там часто пиктограммы)
                roi = img[0:min(200, h//4), max(0, w-w//4):w]
                if roi.size > 0:
                    output_filename = f"{article}_corner.jpg"
                    output_path = os.path.join(pictogram_dir, output_filename)
                    try:
                        # Конвертируем BGR в RGB для PIL
                        roi_rgb = cv2.cvtColor(roi, cv2.COLOR_BGR2RGB)
                        pil_roi = Image.fromarray(roi_rgb)
                        pil_roi.save(output_path, 'JPEG', quality=95)
                        if os.path.exists(output_path):
                            saved_paths.append(output_path)
                            saved_count += 1
                            print(f"    Сохранен угол изображения: {output_path}")
                    except Exception as e:
                        print(f"    Ошибка сохранения угла: {e}")
        
    except Exception as e:
        print(f"  Ошибка при извлечении пиктограмм: {e}")
        import traceback
        traceback.print_exc()
    
    return saved_paths

def extract_and_save_brand(image_path: str, article: str, brand_name: str) -> Optional[str]:
    """Извлекает и сохраняет логотип бренда с этикетки."""
    try:
        if not os.path.exists(image_path) or brand_name == "Не определен":
            return None
        
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
                return None
        
        # Ищем регион в верхней части изображения (там обычно логотипы)
        h, w = img.shape[:2]
        top_region = img[0:int(h*0.3), :]  # Верхние 30% изображения
        
        # Извлекаем регионы из верхней части
        regions = extract_regions_by_contours(top_region, min_area=300)
        
        if regions:
            # Берем самый большой регион из верхней части
            largest_region = max(regions, key=lambda r: r[2] * r[3])
            x, y, w_roi, h_roi = largest_region
            
            # Создаем каталог для бренда
            brand_dir = os.path.join(BRANDS_OUTPUT_DIR, brand_name.replace('/', '_').replace('\\', '_'))
            os.makedirs(brand_dir, exist_ok=True)
            
            # Сохраняем
            output_filename = f"{article}_logo.jpg"
            output_path = os.path.join(brand_dir, output_filename)
            
            roi = img[y:y+h_roi, x:x+w_roi]
            if roi.size > 0:
                cv2.imwrite(output_path, roi)
                return output_path
        
    except Exception as e:
        print(f"  Ошибка при извлечении бренда: {e}")
    
    return None

def detect_brand(text: str, image_path: str) -> Tuple[str, str]:
    """Определяет бренд по тексту и пути к файлу."""
    text_lower = text.lower()
    
    for brand_key, brand_info in BRANDS.items():
        for keyword in brand_info['keywords']:
            if keyword in text_lower:
                return brand_info['name'], brand_key
    
    path_lower = image_path.lower()
    for brand_key, brand_info in BRANDS.items():
        for keyword in brand_info['keywords']:
            if keyword in path_lower:
                return brand_info['name'], brand_key
    
    return "Не определен", ""

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
    print("ОБРАБОТКА ЭТИКЕТОК С ИЗВЛЕЧЕНИЕМ ЭЛЕМЕНТОВ")
    if MAX_ARTICLES_TO_PROCESS:
        print(f"Обработаем максимум {MAX_ARTICLES_TO_PROCESS} артикулов")
    else:
        print("Обработаем ВСЕ артикулы")
    print(f"Результаты будут сохранены в: {OUTPUT_DIR}")
    print("=" * 80)
    
    articles_files = scan_catalogs()
    
    if not articles_files:
        print("Не найдено файлов с артикулами!")
        return
    
    print(f"\nНайдено {len(articles_files)} уникальных артикулов")
    if MAX_ARTICLES_TO_PROCESS:
        print(f"Обработаем первые {min(MAX_ARTICLES_TO_PROCESS, len(articles_files))} артикулов\n")
        articles_to_process = list(articles_files.items())[:MAX_ARTICLES_TO_PROCESS]
    else:
        print(f"Обработаем ВСЕ {len(articles_files)} артикулов\n")
        articles_to_process = list(articles_files.items())
    
    results = []
    processed_count = 0
    
    for article, file_paths in articles_to_process:
        processed_count += 1
        print(f"\n[{processed_count}/{MAX_ARTICLES_TO_PROCESS}] Обработка артикула: {article}")
        print(f"  Найдено файлов: {len(file_paths)}")
        
        if MAX_FILES_PER_ARTICLE:
            files_to_process = file_paths[:MAX_FILES_PER_ARTICLE]
        else:
            files_to_process = file_paths
        
        all_pictograms = set()
        all_texts = []
        all_text_blocks = []
        detected_brand = "Не определен"
        detected_brand_key = ""
        brand_logo_path = None
        saved_pictogram_paths = []
        
        for file_path in files_to_process:
            print(f"  Файл: {os.path.basename(file_path)}")
            
            # Извлекаем текст
            text = extract_text_from_label(file_path)
            if text:
                all_texts.append(text)
                print(f"  Извлечено символов: {len(text)}")
            
            # Извлекаем текстовые блоки
            text_blocks = extract_text_blocks(file_path)
            if text_blocks:
                all_text_blocks.extend(text_blocks)
                print(f"  Найдено текстовых блоков: {len(text_blocks)}")
            
            # Определяем бренд
            brand_name, brand_key = detect_brand(text, file_path)
            if brand_name != "Не определен":
                detected_brand = brand_name
                detected_brand_key = brand_key
                print(f"  Найден бренд: {detected_brand}")
                
                # Извлекаем и сохраняем логотип бренда
                brand_logo_path = extract_and_save_brand(file_path, article, detected_brand)
                if brand_logo_path:
                    print(f"  Сохранен логотип бренда: {brand_logo_path}")
            
            # Определяем пиктограммы
            pictograms = detect_pictograms(file_path)
            all_pictograms.update(pictograms)
            if pictograms:
                print(f"  Найдены пиктограммы: {', '.join(pictograms)}")
                
                # Извлекаем и сохраняем пиктограммы
                saved_paths = extract_and_save_pictograms(file_path, article, list(pictograms))
                saved_pictogram_paths.extend(saved_paths)
                if saved_paths:
                    print(f"  Сохранено пиктограмм: {len(saved_paths)}")
        
        # Формируем данные
        pictograms_str = ', '.join(sorted(all_pictograms)) if all_pictograms else "Не найдено"
        combined_text = ' | '.join(all_texts) if all_texts else "Не извлечен"
        
        # Формируем текстовые блоки (разделенные по блокам)
        text_blocks_str = ''
        if all_text_blocks:
            # Объединяем блоки с разделителями
            text_blocks_str = ' || '.join([f"Блок {i+1}: {block}" for i, block in enumerate(all_text_blocks)])
        
        files_list = '; '.join([os.path.basename(f) for f in files_to_process])
        
        # Получаем дату файла (дату изменения)
        file_date = ""
        file_datetime = None
        if files_to_process and os.path.exists(files_to_process[0]):
            try:
                # Получаем время изменения файла
                timestamp = os.path.getmtime(files_to_process[0])
                file_datetime = datetime.fromtimestamp(timestamp)
                file_date = file_datetime.strftime('%Y-%m-%d %H:%M:%S')
            except Exception as e:
                file_date = "Не определена"
        
        # Создаем запись с колонками для каждой пиктограммы
        record = {
            'Артикул': article,
            'Дата_файла': file_date,
            'Дата_файла_только_дата': file_datetime.strftime('%Y-%m-%d') if file_datetime else "",
            'Количество_файлов': len(file_paths),
            'Файлы': files_list,
            'Первый_файл': os.path.basename(file_paths[0]),
            'Путь_к_первому_файлу': file_paths[0],
            'Бренд': detected_brand,
            'Путь_к_логотипу_бренда': brand_logo_path if brand_logo_path else "Не сохранен",
            'Пиктограммы_найдены': pictograms_str,
            'Текст_полный': combined_text,
            'Текстовые_блоки': text_blocks_str if text_blocks_str else "Не извлечены",
        }
        
        # Добавляем колонки для каждой пиктограммы из базы данных (Да/Нет)
        for pict_key, pict_info in PICTOGRAMS_DB.items():
            pict_name = pict_info['name']
            # Проверяем, найдена ли эта пиктограмма
            found = 'Да' if any(p.lower() == pict_name.lower() or pict_name.lower() in p.lower() 
                              for p in all_pictograms) else 'Нет'
            record[f'Пиктограмма_{pict_name}'] = found
        
        results.append(record)
    
    if results:
        df = pd.DataFrame(results)
        output_file = 'этикетки_с_извлечением.xlsx'
        df.to_excel(output_file, index=False, engine='openpyxl')
        
        print("\n" + "=" * 80)
        print(f"Результаты сохранены в файл: {output_file}")
        print(f"Извлеченные элементы сохранены в: {OUTPUT_DIR}")
        print(f"Обработано артикулов: {len(results)}")
        print("=" * 80)
        
        # Показываем структуру созданных каталогов
        print("\nСтруктура созданных каталогов:")
        if os.path.exists(PICTOGRAMS_OUTPUT_DIR):
            pict_dirs = [d for d in os.listdir(PICTOGRAMS_OUTPUT_DIR) if os.path.isdir(os.path.join(PICTOGRAMS_OUTPUT_DIR, d))]
            print(f"  Пиктограммы: {len(pict_dirs)} каталогов")
            for d in pict_dirs[:5]:
                files_count = len([f for f in os.listdir(os.path.join(PICTOGRAMS_OUTPUT_DIR, d)) if f.endswith('.jpg')])
                print(f"    - {d}: {files_count} файлов")
        
        if os.path.exists(BRANDS_OUTPUT_DIR):
            brand_dirs = [d for d in os.listdir(BRANDS_OUTPUT_DIR) if os.path.isdir(os.path.join(BRANDS_OUTPUT_DIR, d))]
            print(f"  Бренды: {len(brand_dirs)} каталогов")
            for d in brand_dirs:
                files_count = len([f for f in os.listdir(os.path.join(BRANDS_OUTPUT_DIR, d)) if f.endswith('.jpg')])
                print(f"    - {d}: {files_count} файлов")
    else:
        print("\nНет данных для сохранения")

if __name__ == "__main__":
    process_labels()
