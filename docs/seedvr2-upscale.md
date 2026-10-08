# SeedVR2: официальный workflow ComfyUI

В AI → Upscale выбирается имя установленной модели, а не workflow. Доступны обычные апскейлеры и SeedVR2; FLUX в список апскейлеров не попадает. DiT и VAE SeedVR2 выбираются также в AI → ComfyUI Settings → Models on ComfyUI. Выбор сохраняется отдельно для каждого сервера.

Preview.23 использует официальный нативный workflow ComfyUI: LoadImage → JoinImageWithAlpha → ResizeImageMaskNode → SeedVR2Preprocess → VAEEncodeTiled → SeedVR2Conditioning → KSampler → VAEDecodeTiled → SeedVR2PostProcessing. Начальные модели — seedvr2_7b_int8_convrot.safetensors и seedvr2_ema_vae_fp16.safetensors, как в официальном шаблоне. Другие совместимые установленные DiT/VAE выбираются в настройках сервера.

Изображение передаётся целиком в одном запросе, увеличивается ×2 или ×4 с Lanczos. Один шаг Euler/simple, CFG 1, denoise 1. Тайлы остаются только внутри VAE: 512 пикселей с перекрытием 128, temporal_size 4096 и temporal_overlap 8. Коррекция цвета — none, как в официальном шаблоне. Альфа LoadImage передаётся непосредственно в JoinImageWithAlpha, который сам преобразует маску в прозрачность.

Внешнее разбиение Composa, повторные запуски с меньшими тайлами и отдельный workflow Custom nodes удалены. Сохранённый идентификатор seedvr2 перенаправляется на официальный seedvr2-native. Памятью модели управляет ComfyUI. Актуальный сервер должен предоставлять встроенные SeedVR2-ноды и ResizeImageMaskNode; Composa не устанавливает веса или ноды.

При отмене или ошибке документ остаётся прежним. Результат применяется одной отменяемой операцией после завершения запроса. Наличие VAE-тайлов не гарантирует, что любая модель поместится в VRAM: при нехватке памяти нужно выбрать совместимую меньшую модель.

Живая проверка: Intel Arc B580 12 ГБ, установленная SeedVR2 3B FP16 и VAE ema_vae_fp16, фото 96 × 144 → 384 × 576. Один запрос выполнен за 18,73 секунды; альфа и Undo/Redo проверены. Выходное изображение осмотрено. Шаблон 7B INT8 в этой проверке не запускался: на тестовом сервере установлена 3B. Ошибка памяти и отмена отдельно проверены тестовым соединением.

## Первичные источники

- [Официальный шаблон ComfyUI](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/utility_seedvr2_7b_int8_upscale_image.json)
- [Нативные SeedVR2-ноды](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_seedvr.py)
- [Обработка альфа-канала](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_compositing.py)
