console.log('viewer.js loaded');

let currentImageIndex = 0;
let imageIds = [];
let currentTool = 'Wwwc';
const element = document.getElementById('viewer');
console.log('viewer element:', element);

const urlParams = new URLSearchParams(window.location.search);
const studyUid = urlParams.get('studyUid');
const seriesUid = urlParams.get('seriesUid');
const baseUrl = window.location.origin;

// 在文件开头添加播放控制相关变量
let isPlaying = false;
let playInterval = null;
let playbackSpeed = 200; // 设置一个固定的合适速度（200ms 每帧）

// 在文件开头添加加载状态变量
let isLoading = false;

// 添加常量配置
const CONFIG = {
    PLAYBACK_SPEED: 200,  // 播放速度(ms)
    ZOOM: {
        MIN_SCALE: 0.3,
        MAX_SCALE: 10,
        STEP: 0.1
    },
    TOOL_COLORS: {
        DEFAULT: 'rgb(255, 255, 0)',
        ACTIVE: 'rgb(0, 255, 0)' 
    }
};

// 优化工具映射
const TOOL_MAP = {
    wwwc: {
        name: 'Wwwc',
        label: '窗宽窗位'
    },
    pan: {
        name: 'Pan',
        label: '平移'
    },
    zoom: {
        name: 'Zoom',
        label: '缩放'
    },
    length: {
        name: 'Length',
        label: '测距'
    },
    angle: {
        name: 'Angle',
        label: '角度'
    },
    rectangle: {
        name: 'RectangleRoi',
        label: '矩形'
    },
    ellipse: {
        name: 'EllipticalRoi',
        label: '椭圆'
    }
};

// 添加缓存和加载状态管理
const imageCache = {
    loaded: new Set(),  // 已加载的图像
    loading: new Set(), // 正在加载的图像
    instances: [],      // 所有实例信息
    totalCount: 0       // 总图像数
};

// 优化错误处理和日志系统
const Logger = {
    levels: {
        INFO: 'INFO',
        WARN: 'WARN',
        ERROR: 'ERROR'
    },
    
    log(level, message, data = null) {
        const timestamp = new Date().toISOString();
        const logMessage = `[${timestamp}] [${level}] ${message}`;
        
        switch(level) {
            case this.levels.ERROR:
                console.error(logMessage, data);
                break;
            case this.levels.WARN:
                console.warn(logMessage, data);
                break;
            default:
                console.log(logMessage, data);
        }
    }
};

// 优化加载状态管理
const LoadingManager = {
    mainIndicator: null,
    statusIndicator: null,
    
    showMainLoading() {
        if (this.mainIndicator) return;
        
        this.mainIndicator = document.createElement('div');
        this.mainIndicator.id = 'loadingIndicator';
        this.mainIndicator.innerHTML = `
            <div class="loading-spinner"></div>
            <div class="loading-text">加载中...</div>
        `;
        document.getElementById('viewer').appendChild(this.mainIndicator);
    },
    
    hideMainLoading() {
        if (this.mainIndicator) {
            this.mainIndicator.remove();
            this.mainIndicator = null;
        }
    },
    
    showStatus(message) {
        if (!this.statusIndicator) {
            this.statusIndicator = document.createElement('div');
            this.statusIndicator.id = 'loadingStatus';
            this.statusIndicator.style.cssText = `
                position: absolute;
                top: 10px;
                right: 10px;
                background: rgba(0, 0, 0, 0.7);
                color: white;
                padding: 5px 10px;
                border-radius: 4px;
                font-size: 12px;
                z-index: 1000;
                transition: opacity 0.3s ease;
            `;
            document.getElementById('viewer').appendChild(this.statusIndicator);
        }
        this.statusIndicator.textContent = message;
    },
    
    hideStatus() {
        if (this.statusIndicator) {
            this.statusIndicator.remove();
            this.statusIndicator = null;
        }
    }
};

// 初始化 Cornerstone
function initializeViewer() {
    try {
        // 配置 cornerstone
        cornerstoneTools.external.cornerstone = cornerstone;
        cornerstoneTools.external.Hammer = Hammer;
        
        // 配置 WADO 图像加载器
        cornerstoneWADOImageLoader.external.cornerstone = cornerstone;
        cornerstoneWADOImageLoader.external.dicomParser = dicomParser;
        
        // 配置 WADO 图像加载器选项
        cornerstoneWADOImageLoader.configure({
            useWebWorkers: false,  // 暂时关闭 WebWorker 以便调试
            decodeConfig: {
                convertFloatPixelDataToInt: false,
                use16Bits: true
            }
        });

        // 注册图像加载器
        cornerstone.registerImageLoader('wadouri', cornerstoneWADOImageLoader.wadouri.loadImage);
        cornerstone.registerImageLoader('wadors', cornerstoneWADOImageLoader.wadors.loadImage);

        // 启用 element
        cornerstone.enable(element);

        // 初始化工具
        initializeTools();
        
        // 设置工具栏事件
        setupToolbar();

        // 添加图像渲染事件监听
        cornerstone.events.addEventListener('cornerstoneimagerendered', onImageRendered);

        // 添加滚轮事件
        element.addEventListener('wheel', function(event) {
            if (!isPlaying) {  // 只在非播放状态下响应滚轮事件
                if (event.deltaY < 0) {
                    displayImage(currentImageIndex - 1);
                } else {
                    displayImage(currentImageIndex + 1);
                }
                event.preventDefault();
            }
        });

        // 修改窗宽窗位事件监听
        element.addEventListener('cornerstoneimagerendered', function(e) {
            const viewport = cornerstone.getViewport(element);
            const image = cornerstone.getImage(element);
            
            // 始终显示当前的窗宽窗位值
            document.getElementById('windowInfo').innerHTML = `
                窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
                窗位: ${Math.round(viewport.voi.windowCenter)}
            `;
        });

        // 修改播放控制事件监听
        const playButton = document.getElementById('playButton');
        playButton.addEventListener('click', togglePlay);

        console.log('[Init] Viewer initialized successfully');
    } catch (error) {
        console.error('[Init] Failed to initialize viewer:', error);
    }
}

// 等待 DOM 加载完成后再初始化
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initializeViewer);
} else {
    initializeViewer();
}

// 优化初始化工具函数
function initializeTools() {
    cornerstoneTools.init();

    // 注册所有工具
    Object.values(TOOL_MAP).forEach(tool => {
        const toolClass = cornerstoneTools[`${tool.name}Tool`];
        if (toolClass) {
            cornerstoneTools.addTool(toolClass);
        }
    });

    // 配置缩放工具
    cornerstoneTools.addTool(cornerstoneTools.ZoomTool, {
        configuration: {
            minScale: CONFIG.ZOOM.MIN_SCALE,
            maxScale: CONFIG.ZOOM.MAX_SCALE,
            preventZoomOutside: true
        }
    });

    // 设置默认工具状态
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });

    // 配置工具样式
    cornerstoneTools.toolStyle.setToolWidth(2);
    cornerstoneTools.toolColors.setToolColor(CONFIG.TOOL_COLORS.DEFAULT);
    cornerstoneTools.toolColors.setActiveColor(CONFIG.TOOL_COLORS.ACTIVE);

    setupToolEvents();
}

// 优化工具事件设置
function setupToolEvents() {
    // 禁用右键菜单
    element.addEventListener('contextmenu', e => e.preventDefault());

    // 优化滚轮事件
    element.addEventListener('wheel', handleMouseWheel);
}

// 优化滚轮事件处理
function handleMouseWheel(e) {
    e.preventDefault();
    
    if (isPlaying) return; // 播放时不响应滚轮

    if (e.shiftKey) {
        handleZoomWheel(e);
    } else {
        handleImageScroll(e);
    }
}

// 分离缩放逻辑
function handleZoomWheel(e) {
    const viewport = cornerstone.getViewport(element);
    const zoomStep = e.deltaY < 0 ? (1 + CONFIG.ZOOM.STEP) : (1 - CONFIG.ZOOM.STEP);
    
    viewport.scale *= zoomStep;
    viewport.scale = Math.max(CONFIG.ZOOM.MIN_SCALE, 
                            Math.min(CONFIG.ZOOM.MAX_SCALE, viewport.scale));
    
    cornerstone.setViewport(element, viewport);
}

// 分离图像切换逻辑
function handleImageScroll(e) {
    const direction = e.deltaY < 0 ? -1 : 1;
    displayImage(currentImageIndex + direction);
}

// 激活工具
function activateTool(toolName) {
    const toolMap = {
        'wwwc': 'Wwwc',
        'pan': 'Pan',
        'zoom': 'Zoom',
        'length': 'Length',
        'angle': 'Angle',
        'rectangle': 'RectangleRoi',
        'ellipse': 'EllipticalRoi'
    };

    const toolToActivate = toolMap[toolName];
    if (toolToActivate) {
        try {
            // 禁用所有工具
            Object.values(toolMap).forEach(tool => {
                cornerstoneTools.setToolPassive(tool);
            });

            // 激活中的工具
            if (toolName === 'zoom') {
                // 缩放工具使用左键
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 1 });
                // 保持平移可用（右键）
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
            } else if (toolName === 'pan') {
                // 平移工具使用左键
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 1 });
                // 保持缩放可用（右键）
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 2 });
            } else {
                // 其他工具使用左键
                cornerstoneTools.setToolActive(toolToActivate, { mouseButtonMask: 1 });
                // 保持平移（右键）和缩放（中键）可用
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
            }

            currentTool = toolToActivate;
            console.log('Tool activated:', toolToActivate);
        } catch (error) {
            console.error('Error activating tool:', error);
        }
    }
}

// 设置工具栏
function setupToolbar() {
    document.querySelectorAll('.tool-button').forEach(button => {
        button.addEventListener('click', handleToolButtonClick);
    });
}

// 工具按钮点击处理
function handleToolButtonClick(e) {
    e.preventDefault();
    e.stopPropagation();

    const toolName = this.dataset.tool;
    if (toolName) {
        // 更新按钮状态
        document.querySelectorAll('.tool-button').forEach(btn => {
            btn.classList.remove('active');
        });
        if (!this.id) {
            this.classList.add('active');
            activateTool(toolName);
        }
    }

    // 特殊按钮处理
    if (this.id === 'resetView') {
        cornerstone.reset(element);
    } else if (this.id === 'clearAnnotations') {
        clearAnnotations();
    }
}

// 清除标注
function clearAnnotations() {
    const toolList = ['Length', 'Angle', 'RectangleRoi', 'EllipticalRoi'];
    toolList.forEach(toolType => {
        cornerstoneTools.clearToolState(element, toolType);
    });
    cornerstone.updateImage(element);
}

// 图像渲染事件处理
function onImageRendered(event) {
    const viewport = cornerstone.getViewport(event.target);
    const image = cornerstone.getImage(event.target);
    updateCornerInfo(image, viewport);
}

// 更新角落信息
function updateCornerInfo(image, viewport) {
    const patientInfo = {
        name: image.data.string('x00100010') || 'N/A',
        id: image.data.string('x00100020') || 'N/A',
        gender: image.data.string('x00100040') || 'N/A'
    };

    const studyInfo = {
        accessionNumber: image.data.string('x00080050') || 'N/A',
        modality: image.data.string('x00080060') || 'N/A',
        studyDate: formatDate(image.data.string('x00080020'))
    };

    // 更新界面显示
    document.getElementById('patientInfo').innerHTML = `
        ${patientInfo.name}<br>
        ID: ${patientInfo.id}<br>
        性别: ${patientInfo.gender}
    `;

    document.getElementById('studyInfo').innerHTML = `
        检查号: ${studyInfo.accessionNumber}<br>
        检查类型: ${studyInfo.modality}<br>
        检查时间: ${studyInfo.studyDate}
    `;

    // 获取总帧数
    const numberOfFrames = image.data.intString('x00280008') || 1;
    const instanceNumber = image.data.string('x00200013') || 'N/A';
    const seriesNumber = image.data.string('x00200011') || 'N/A';

    // 从 imageId 中获取当前帧号
    let currentFrame = 0;
    const imageId = image.imageId;
    if (imageId.includes('?frame=')) {
        currentFrame = parseInt(imageId.split('?frame=')[1]);
    }

    // 如果是多帧图像，显示帧信息；否则显示图像号
    document.getElementById('imageInfo').innerHTML = `
        序列号: ${seriesNumber}<br>
        ${numberOfFrames > 1 ? '帧号' : '图像号'}: ${numberOfFrames > 1 ? (currentFrame + 1) : instanceNumber}<br>
        ${currentImageIndex + 1}/${imageIds.length}
    `;

    document.getElementById('windowInfo').innerHTML = `
        窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
        窗位: ${Math.round(viewport.voi.windowCenter)}
    `;
}

// 格式日期
function formatDate(dateStr) {
    if (!dateStr) return 'N/A';
    return `${dateStr.slice(0,4)}-${dateStr.slice(4,6)}-${dateStr.slice(6,8)}`;
}

// 优化图像加载函数
async function loadImages() {
    try {
        LoadingManager.showMainLoading();
        
        // 获取实例列表
        Logger.log(Logger.levels.INFO, 'Fetching instances', { studyUid, seriesUid });
        const response = await axios.get(`/api/images/${studyUid}/series/${seriesUid}/instances`);
        
        if (!response.data?.length) {
            throw new Error('No instances found');
        }

        imageCache.instances = response.data;
        imageCache.totalCount = response.data.length;
        
        // 加载第一张图像
        await loadAndDisplayFirstImage();
        LoadingManager.hideMainLoading();
        
        // 后台加载其他图像
        loadRemainingImages();
        
    } catch (error) {
        Logger.log(Logger.levels.ERROR, 'Failed to load images', error);
        alert(error.response?.data?.message || error.message || '加载图像失败');
        LoadingManager.hideMainLoading();
    }
}

// 优化后台加载函数
async function loadRemainingImages() {
    try {
        const loadPromises = [];
        
        for (let i = 1; i < imageCache.instances.length; i++) {
            const instance = imageCache.instances[i];
            const imageId = `wadouri:${baseUrl}/api/images/download/${instance.sopInstanceUid}`;
            
            if (!imageCache.loaded.has(imageId) && !imageCache.loading.has(imageId)) {
                imageCache.loading.add(imageId);
                
                const loadPromise = (async () => {
                    try {
                        LoadingManager.showStatus(`加载中: ${i + 1}/${imageCache.totalCount}`);
                        const image = await cornerstone.loadAndCacheImage(imageId);
                        
                        handleImageLoaded(image, imageId);
                        imageCache.loaded.add(imageId);
                        
                    } catch (error) {
                        Logger.log(Logger.levels.ERROR, `Failed to load image ${i + 1}`, error);
                    } finally {
                        imageCache.loading.delete(imageId);
                    }
                })();
                
                loadPromises.push(loadPromise);
            }
        }
        
        // 并行加载，但限制并发数
        const CONCURRENT_LOADS = 3;
        for (let i = 0; i < loadPromises.length; i += CONCURRENT_LOADS) {
            await Promise.all(loadPromises.slice(i, i + CONCURRENT_LOADS));
        }
        
    } finally {
        LoadingManager.hideStatus();
    }
}

// 优化图像处理函数
function handleImageLoaded(image, imageId) {
    const numberOfFrames = image.data.intString('x00280008') || 1;
    
    if (numberOfFrames > 1) {
        for (let frameIndex = 0; frameIndex < numberOfFrames; frameIndex++) {
            imageIds.push(`${imageId}?frame=${frameIndex}`);
        }
    } else {
        imageIds.push(imageId);
    }
}

// 优化显示图像函数
async function displayImage(index) {
    if (index < 0 || index >= imageIds.length) {
        Logger.log(Logger.levels.WARN, 'Invalid image index', { index });
        return;
    }
    
    try {
        const imageId = imageIds[index];
        const image = await cornerstone.loadAndCacheImage(imageId);
        const viewport = await getOptimizedViewport(image);
        
        await cornerstone.displayImage(element, image, viewport);
        currentImageIndex = index;
        updateCornerInfo(image, viewport);
        
    } catch (error) {
        Logger.log(Logger.levels.ERROR, 'Failed to display image', { error, index });
    }
}

// 优化视口获取
async function getOptimizedViewport(image) {
    const viewport = cornerstone.getDefaultViewportForImage(element, image);
    const currentViewport = cornerstone.getViewport(element);
    
    if (currentViewport) {
        Object.assign(viewport, {
            scale: currentViewport.scale,
            translation: currentViewport.translation,
            voi: currentTool === 'Wwwc' && !isPlaying 
                ? currentViewport.voi 
                : viewport.voi
        });
    }
    
    return viewport;
}

// 修改播放控制函数
function togglePlay() {
    if (isPlaying) {
        pausePlay();
    } else {
        startPlay();
    }
}

function startPlay() {
    if (!isPlaying && imageIds.length > 1) {
        isPlaying = true;
        const playButton = document.getElementById('playButton');
        playButton.innerHTML = '<img src="images/tools/pause.svg" alt="暂停" width="20" height="20">';
        
        playInterval = setInterval(() => {
            let nextIndex = currentImageIndex + 1;
            if (nextIndex >= imageIds.length) {
                nextIndex = 0; // 循环播放
            }
            displayImage(nextIndex);
        }, playbackSpeed);
    }
}

function pausePlay() {
    if (isPlaying) {
        isPlaying = false;
        const playButton = document.getElementById('playButton');
        playButton.innerHTML = '<img src="images/tools/play.svg" alt="播放" width="20" height="20">';
        
        if (playInterval) {
            clearInterval(playInterval);
            playInterval = null;
        }
    }
}

// 修改播放功能相关的代码
function playImages() {
    if (isPlaying) {
        currentImageIndex = (currentImageIndex + 1) % imageIds.length;
        displayImage(currentImageIndex);
        // 使用 CONFIG.PLAYBACK_SPEED 替代 playbackSpeed
        setTimeout(playImages, CONFIG.PLAYBACK_SPEED);
    }
}

// 添加加载并显示第一张图像的函数
async function loadAndDisplayFirstImage() {
    try {
        const firstInstance = imageCache.instances[0];
        Logger.log(Logger.levels.INFO, 'Loading first instance', firstInstance);
        
        if (!firstInstance?.sopInstanceUid) {
            throw new Error('Invalid first instance data');
        }

        const imageId = `wadouri:${baseUrl}/api/images/download/${firstInstance.sopInstanceUid}`;
        Logger.log(Logger.levels.INFO, 'Loading first image', { imageId });
        
        // 加载第一张图像
        const image = await cornerstone.loadAndCacheImage(imageId);
        
        // 处理多帧图像
        const numberOfFrames = image.data.intString('x00280008') || 1;
        
        // 重置 imageIds 数组
        imageIds = [];
        
        if (numberOfFrames > 1) {
            // 如果是多帧图像，添加所有帧
            imageIds = Array.from(
                { length: numberOfFrames }, 
                (_, i) => `${imageId}?frame=${i}`
            );
        } else {
            imageIds = [imageId];
        }
        
        // 标记为已加载
        imageCache.loaded.add(imageId);
        
        // ��即显示第一张图像
        await displayImage(0);
        
        Logger.log(Logger.levels.INFO, 'First image loaded and displayed', {
            numberOfFrames,
            totalImages: imageIds.length
        });
        
        return true;
    } catch (error) {
        Logger.log(Logger.levels.ERROR, 'Failed to load first image', {
            error,
            stack: error.stack
        });
        throw error;
    }
}

// 初始化并加载图像
initializeViewer();
loadImages();