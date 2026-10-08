# SeedVR2 и автоматические тайлы

В **AI → Upscale** можно выбрать обычный апскейлер, **SeedVR2 · Native · Auto tiles** или **SeedVR2 · Custom nodes · Auto tiles**. Размер результата — ×2 или ×4. В **AI → ComfyUI Settings → Workflows & models** выберите установленные модели для соответствующего пака.

Нативный вариант использует встроенные SeedVR2Preprocess, SeedVR2Conditioning и SeedVR2PostProcessing в актуальном ComfyUI. Начальные имена моделей: seedvr2_3b_fp16.safetensors и ema_vae_fp16.safetensors; другие совместимые SeedVR2-файлы выбираются из списка сервера. Официальный пример ComfyUI использует 7B INT8 и один шаг Euler/simple с CFG 1. В Composa сохранены этот порядок стадий и параметры; исходное изображение равномерно увеличивается перед VAE, альфа восстанавливается после обработки.

Вариант Custom nodes использует пакет **ComfyUI-SeedVR2_VideoUpscaler** с SeedVR2LoadDiTModel, SeedVR2LoadVAEModel и SeedVR2VideoUpscaler. Его модели и устройства также читаются с сервера. Сам пакет и веса Composa не устанавливает. Для маленькой VRAM авторы рекомендуют GGUF Q4 и BlockSwap; для 12–16 ГБ — FP8. Эти рекомендации относятся к этому пакету, а не к совместимости нативного UNETLoader с GGUF.

Composa обрабатывает весь SeedVR2-процесс по перекрывающимся тайлам, последовательно, по одному изображению. Ограничивается размер **выходного тайла вместе с перекрытием**, а не только VAE. Изображение перед апскейлом не уменьшается. Размер определяется по свежим vram_total/vram_free из system_stats, с резервом; если информация отсутствует, применяется консервативный вариант. Дополнительно включён VAE tiling. В Custom nodes блоки DiT и промежуточные тензоры выгружаются в RAM, без постоянного GPU-кэша; нативный вариант использует управление весами ComfyUI.

При сообщении out-of-memory обработка начинает заново с меньшими тайлами, до ограниченного минимального размера. Другие ошибки не маскируются повторными попытками. Отмена и ошибки оставляют документ без изменений; результат добавляется одной отменяемой операцией после завершения всех тайлов. Перекрытия смешиваются по плавным весам на CPU.

Автоматика уменьшает риск нехватки памяти, но не гарантирует запуск любой модели на любой видеокарте: весам тоже нужна память, соседние запросы меняют её доступность. Если минимального тайла недостаточно, выберите меньшую/квантованную совместимую модель или освободите память сервера. Стык тайлов на сложной фактуре может остаться заметным.

## Первичные источники

- [Официальный нативный шаблон ComfyUI](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/utility_seedvr2_7b_int8_upscale_image.json)
- [Нативные SeedVR2-ноды ComfyUI](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_seedvr.py)
- [Документация ComfyUI-SeedVR2_VideoUpscaler](https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler)
- [Схема загрузчика DiT](https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler/blob/main/src/interfaces/dit_model_loader.py)
- [Схема загрузчика VAE](https://github.com/numz/ComfyUI-SeedVR2_VideoUpscaler/blob/main/src/interfaces/vae_model_loader.py)
