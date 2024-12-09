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

// 工具映射配置
const TOOL_MAP = {
    Wwwc: {
        name: 'Wwwc',
        label: '窗宽窗位',
        icon: 'window.svg',
        mouseButton: 1
    },
    Pan: {
        name: 'Pan',
        label: '平移',
        icon: 'pan.svg',
        mouseButton: 1
    },
    Zoom: {
        name: 'Zoom',
        label: '缩放',
        icon: 'zoom.svg',
        mouseButton: 1
    },
    Magnify: {
        name: 'Magnify',
        label: '放大镜',
        icon: 'magnify.svg',
        mouseButton: 1,
        configuration: {
            magnifySize: 300,
            magnificationLevel: 4
        }
    },
    Length: {
        name: 'Length',
        label: '测距',
        icon: 'length.svg',
        mouseButton: 1
    },
    Angle: {
        name: 'Angle',
        label: '角度',
        icon: 'angle.svg',
        mouseButton: 1
    },
    Rectangle: {
        name: 'RectangleRoi',
        label: '矩形',
        icon: 'rectangle.svg',
        mouseButton: 1
    },
    Ellipse: {
        name: 'EllipticalRoi',
        label: '椭圆',
        icon: 'ellipse.svg',
        mouseButton: 1
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
                padding: 8px 15px;
                border-radius: 4px;
                font-size: 14px;
                z-index: 1000;
                transition: opacity 0.3s ease;
                box-shadow: 0 2px 8px rgba(0, 0, 0, 0.2);
            `;
            document.getElementById('viewer').appendChild(this.statusIndicator);
        }
        this.statusIndicator.textContent = message;
        this.statusIndicator.style.opacity = '1';
    },
    
    hideStatus() {
        if (this.statusIndicator) {
            this.statusIndicator.style.opacity = '0';
            setTimeout(() => {
                if (this.statusIndicator) {
                    this.statusIndicator.remove();
                    this.statusIndicator = null;
                }
            }, 300);
        }
    }
};

// 添加 DICOM Tags 相关功能
const DicomTagsViewer = {
    modal: null,
    searchBox: null,
    tableBody: null,
    allTags: [],

    init() {
        // 检查必要的 DOM 元素是否存在
        this.modal = document.getElementById('dicomTagsModal');
        if (!this.modal) {
            throw new Error('DICOM Tags modal not found');
        }

        this.searchBox = this.modal.querySelector('.search-box');
        if (!this.searchBox) {
            throw new Error('Search box not found');
        }

        this.tableBody = document.getElementById('tagsTableBody');
        if (!this.tableBody) {
            throw new Error('Tags table body not found');
        }

        // 绑定按钮事件
        const showButton = document.getElementById('showDicomTags');
        if (!showButton) {
            throw new Error('Show DICOM Tags button not found');
        }
        showButton.addEventListener('click', () => this.show());
        
        // 绑定关闭按钮
        const closeButton = this.modal.querySelector('.close-button');
        if (!closeButton) {
            throw new Error('Close button not found');
        }
        closeButton.addEventListener('click', () => this.hide());
        
        // 绑搜索事件
        this.searchBox.addEventListener('input', () => this.filterTags());
        
        // 点击模态框外部关闭
        this.modal.addEventListener('click', (e) => {
            if (e.target === this.modal) this.hide();
        });

        console.log('[DicomTagsViewer] Initialized successfully');
    },

    show() {
        const image = cornerstone.getImage(element);
        if (!image) return;

        this.allTags = this.getAllTags(image.data);
        this.renderTags(this.allTags);
        this.modal.style.display = 'block';
    },

    hide() {
        this.modal.style.display = 'none';
        this.searchBox.value = '';
    },

    getAllTags(dataset) {
        const tags = [];
        for (let tag in dataset.elements) {
            const element = dataset.elements[tag];
            const tagInfo = this.getTagInfo(tag, element, dataset);
            if (tagInfo) {
                tags.push(tagInfo);
            }
        }
        return tags.sort((a, b) => a.tag.localeCompare(b.tag));
    },

    getTagInfo(tag, element, dataset) {
        try {
            const value = dataset.string(tag);
            const vr = element.vr;
            const tagGroup = tag.substring(1, 5);
            const tagElement = tag.substring(5, 9);
            const description = this.getTagDescription(tagGroup, tagElement);

            return {
                tag: `(${tagGroup},${tagElement})`,
                vr: vr || '',
                description: description || '',
                value: value || ''
            };
        } catch (error) {
            return null;
        }
    },

    renderTags(tags) {
        this.tableBody.innerHTML = tags.map(tag => `
            <tr>
                <td class="tag-group">${tag.tag}</td>
                <td class="tag-vr">${tag.vr}</td>
                <td>${tag.description}</td>
                <td class="tag-value">${tag.value}</td>
            </tr>
        `).join('');
    },

    filterTags() {
        const searchText = this.searchBox.value.toLowerCase();
        const filteredTags = this.allTags.filter(tag => 
            tag.tag.toLowerCase().includes(searchText) ||
            tag.description.toLowerCase().includes(searchText) ||
            tag.value.toLowerCase().includes(searchText)
        );
        this.renderTags(filteredTags);
    },

    getTagDescription(group, element) {
        // Common DICOM tag descriptions
        const commonTags = {
            // Patient Information
            '0010,0010': 'Patient Name',
            '0010,0020': 'Patient ID',
            '0010,0030': 'Patient Birth Date',
            '0010,0040': 'Patient Sex',
            '0010,1010': 'Patient Age',
            '0010,1020': 'Patient Size',
            '0010,1030': 'Patient Weight',

            // Study Information
            '0008,0020': 'Study Date',
            '0008,0021': 'Series Date',
            '0008,0022': 'Acquisition Date',
            '0008,0023': 'Image Date',
            '0008,0030': 'Study Time',
            '0008,0031': 'Series Time',
            '0008,0032': 'Acquisition Time',
            '0008,0033': 'Image Time',
            '0008,0050': 'Accession Number',
            '0008,0060': 'Modality',
            '0008,0070': 'Manufacturer',
            '0008,0080': 'Institution Name',
            '0008,1030': 'Study Description',
            '0008,103E': 'Series Description',
            '0008,1090': 'Manufacturer Model',

            // Series Information
            '0020,0010': 'Study ID',
            '0020,0011': 'Series Number',
            '0020,0012': 'Acquisition Number',
            '0020,0013': 'Instance Number',
            '0020,0032': 'Image Position',
            '0020,0037': 'Image Orientation',
            '0020,1041': 'Slice Location',

            // Image Information
            '0028,0002': 'Samples per Pixel',
            '0028,0004': 'Photometric Interpretation',
            '0028,0008': 'Number of Frames',
            '0028,0010': 'Rows',
            '0028,0011': 'Columns',
            '0028,0030': 'Pixel Spacing',
            '0028,0100': 'Bits Allocated',
            '0028,0101': 'Bits Stored',
            '0028,1050': 'Window Center',
            '0028,1051': 'Window Width',

            // CT Specific
            '0018,0022': 'Scan Options',
            '0018,0050': 'Slice Thickness',
            '0018,0060': 'KVP',
            '0018,1100': 'Reconstruction Diameter',
            '0018,1120': 'Gantry/Detector Tilt',
            '0018,1130': 'Table Height',
            '0018,1150': 'Exposure Time',
            '0018,1151': 'X-ray Tube Current',
            '0018,1152': 'Exposure',
            '0018,1160': 'Filter Type',
            '0018,1210': 'Convolution Kernel',

            // MR Specific
            '0018,0020': 'Scanning Sequence',
            '0018,0021': 'Sequence Variant',
            '0018,0023': 'Acquisition Type',
            '0018,0024': 'Sequence Name',
            '0018,0080': 'Repetition Time',
            '0018,0081': 'Echo Time',
            '0018,0082': 'Inversion Time',
            '0018,0083': 'Number of Averages',
            '0018,0087': 'Magnetic Field Strength',
            '0018,0088': 'Spacing Between Slices',
            '0018,0089': 'Number of Phase Encoding Steps',
            '0018,0091': 'Echo Train Length',
            '0018,0095': 'Pixel Bandwidth',

            // General Parameters
            '0018,1000': 'Device Serial Number',
            '0018,1020': 'Software Version',
            '0018,1030': 'Protocol Name',
            '0018,1040': 'Contrast/Bolus Agent',
            '0018,1041': 'Contrast/Bolus Volume',
            '0018,1046': 'Contrast Flow Rate',

            // Other Important Information
            '0032,1032': 'Requesting Physician',
            '0032,1033': 'Requesting Service',
            '0040,0002': 'Scheduled Procedure Step Start Date',
            '0040,0003': 'Scheduled Procedure Step Start Time',
            '0040,0009': 'Scheduled Procedure Step ID',
            '0040,0010': 'Scheduled Station Name'
        };
        
        const key = `${group},${element}`;
        return commonTags[key] || '';
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
            useWebWorkers: false,
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
            if (!isPlaying) {
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
            
            document.getElementById('windowInfo').innerHTML = `
                窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
                窗位: ${Math.round(viewport.voi.windowCenter)}
            `;
        });

        // 修改播放控制事件监听
        const playButton = document.getElementById('playButton');
        playButton.addEventListener('click', togglePlay);

        console.log('[Init] Viewer initialized successfully');
        
        // 修改 DICOM Tags 查看器的初始化时机
        window.addEventListener('load', () => {
            try {
                DicomTagsViewer.init();
                console.log('[Init] DICOM Tags viewer initialized successfully');
            } catch (error) {
                console.error('[Init] Failed to initialize DICOM Tags viewer:', error);
            }
        });

    } catch (error) {
        console.error('[Init] Failed to initialize viewer:', error);
    }
}

// 修改初始化调用
document.addEventListener('DOMContentLoaded', () => {
    initializeViewer();
    loadImages();
});

// 优化初始化工具函数
function initializeTools() {
    cornerstoneTools.init();

    // 注册所有工具
    Object.values(TOOL_MAP).forEach(tool => {
        const toolClass = cornerstoneTools[`${tool.name}Tool`];
        if (toolClass) {
            if (tool.configuration) {
                cornerstoneTools.addTool(toolClass, tool.configuration);
            } else {
                cornerstoneTools.addTool(toolClass);
            }
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

    // 配置工具样式
    cornerstoneTools.toolStyle.setToolWidth(2);
    cornerstoneTools.toolColors.setToolColor(CONFIG.TOOL_COLORS.DEFAULT);
    cornerstoneTools.toolColors.setActiveColor(CONFIG.TOOL_COLORS.ACTIVE);

    // 设置默认工具状态
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });

    // 初始化翻层工具
    cornerstoneTools.addTool(cornerstoneTools.StackScrollTool);
    cornerstoneTools.setToolActive('StackScroll', { mouseButtonMask: 1 });

    // 初始化探针工具
    cornerstoneTools.addTool(cornerstoneTools.ProbeTool);
    
    // 工具按钮点击事件处理
    const toolButtons = document.querySelectorAll('.tool-button[data-tool]');
    toolButtons.forEach(button => {
        button.addEventListener('click', (e) => {
            // 移除所有工具按钮的 active 类
            toolButtons.forEach(btn => btn.classList.remove('active'));
            // 为当前点击的按钮添加 active 类
            button.classList.add('active');

            // 获取工具名称
            const tool = button.getAttribute('data-tool');

            // 停用所有工具
            disableAllTools();

            // 根据工具类型激活相应的工具
            switch (tool) {
                case 'Stack':
                    cornerstoneTools.setToolActive('StackScroll', { mouseButtonMask: 1 });
                    break;
                case 'Probe':
                    cornerstoneTools.setToolActive('Probe', { mouseButtonMask: 1 });
                    break;
                // ... 其他工具的 case
            }
        });
    });

    setupToolEvents();
}

// 禁用所有工具
function disableAllTools() {
    cornerstoneTools.setToolDisabled('StackScroll');
    cornerstoneTools.setToolDisabled('Probe');
    // ... 禁用其他工具
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
    
    if (isPlaying) return;

    if (e.shiftKey) {
        handleZoomWheel(e);
    } else {
        handleImageScroll(e);
    }
}

// 优化缩放逻辑
function handleZoomWheel(e) {
    const viewport = cornerstone.getViewport(element);
    const zoomStep = CONFIG.ZOOM.STEP * CONFIG.ZOOM.MOUSE_SENSITIVITY;
    const zoomFactor = e.deltaY < 0 ? (1 + zoomStep) : (1 - zoomStep);
    
    viewport.scale *= zoomFactor;
    viewport.scale = Math.max(CONFIG.ZOOM.MIN_SCALE, 
                            Math.min(CONFIG.ZOOM.MAX_SCALE, viewport.scale));
    
    // 添加平滑动
    requestAnimationFrame(() => {
        cornerstone.setViewport(element, viewport);
    });
}

// 分离图像切换逻辑
function handleImageScroll(e) {
    const direction = e.deltaY < 0 ? -1 : 1;
    displayImage(currentImageIndex + direction);
}

// 激活工具
function activateTool(toolName) {
    const tool = TOOL_MAP[toolName];
    if (!tool) return;

    try {
        // 停用所有工具
        Object.values(TOOL_MAP).forEach(t => {
            cornerstoneTools.setToolPassive(t.name);
        });

        // 激活选中的工具
        if (toolName === 'Zoom') {
            // 缩放工具使用左键
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 1 });
            // 保持平移可用（右键）
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
        } else if (toolName === 'Pan') {
            // 平移工具使用左键
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 1 });
            // 保持缩放可用（右键）
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 2 });
        } else {
            // 其他工具使用左键
            cornerstoneTools.setToolActive(tool.name, { mouseButtonMask: 1 });
            // 保持平移（右键）和缩放（中键）可用
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
        }

        currentTool = tool.name;
        Logger.log(Logger.levels.INFO, `工具已激活: ${tool.label}`);
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '激活工具失败:', error);
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
    if (!toolName) {
        // 处理特殊按钮
        if (this.id === 'resetView') {
            resetView();
        } else if (this.id === 'clearAnnotations') {
            clearAnnotations();
        } else if (this.id === 'showDicomTags') {
            DicomTagsViewer.show();
        }
        return;
    }

    // 处理特殊工具
    switch(toolName) {
        case 'flip-horizontal':
            flipImage('horizontal');
            return;
        case 'flip-vertical':
            flipImage('vertical');
            return;
        case 'rotate-right':
            rotateImage(90);
            return;
        case 'rotate-left':
            rotateImage(-90);
            return;
        case 'invert':
            invertImage();
            return;
    }

    // 处理常规工具
    document.querySelectorAll('.tool-button').forEach(btn => {
        btn.classList.remove('active');
    });
    this.classList.add('active');
    activateTool(toolName);
}

// 清除标注
function clearAnnotations() {
    const toolList = ['Length', 'Angle', 'RectangleRoi', 'EllipticalRoi'];
    toolList.forEach(toolType => {
        cornerstoneTools.clearToolState(element, toolType);
    });
    cornerstone.updateImage(element);
    Logger.log(Logger.levels.INFO, '标注已清除');
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

    // 如果是多帧图像，显示帧信息否则显示图像号
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
        
        // 后台载其他图像
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
                        const percentage = Math.round((i + 1) / imageCache.totalCount * 100);
                        LoadingManager.showStatus(`加载中: ${i + 1}/${imageCache.totalCount} (${percentage}%)`);
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
        if (imageCache.loaded.size === imageCache.totalCount) {
            LoadingManager.hideStatus();
        }
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

    // 为探针工具添加事件监听
    element.addEventListener('cornerstonetoolsmeasurementcompleted', function(e) {
        if (e.detail.toolType === 'Probe') {
            const data = e.detail.measurementData;
            // 在这里可以处理探针测量的数据
            console.log('探针值:', data.currentPoints.image);
        }
    });

    // 如果是多帧图像，设置堆栈状态
    if (imageIds.length > 1) {
        const stack = {
            currentImageIdIndex: 0,
            imageIds: imageIds
        };
        
        cornerstoneTools.addStackStateManager(element, ['stack']);
        cornerstoneTools.addToolState(element, 'stack', stack);
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
        
        // 立即显示第一张图像
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

// 添加图像翻转功能
function flipImage(direction) {
    try {
        const viewport = cornerstone.getViewport(element);
        
        if (direction === 'horizontal') {
            viewport.hflip = !viewport.hflip;
            Logger.log(Logger.levels.INFO, '水平翻转');
        } else {
            viewport.vflip = !viewport.vflip;
            Logger.log(Logger.levels.INFO, '垂直翻转');
        }
        
        cornerstone.setViewport(element, viewport);
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '翻转图像失败', error);
    }
}

// 添加图像旋转功能
function rotateImage(angle) {
    try {
        const viewport = cornerstone.getViewport(element);
        viewport.rotation = ((viewport.rotation || 0) + angle) % 360;
        cornerstone.setViewport(element, viewport);
        Logger.log(Logger.levels.INFO, `旋转 ${angle} 度`);
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '旋转图像失败', error);
    }
}

// 添加图像反相功能
function invertImage() {
    try {
        const viewport = cornerstone.getViewport(element);
        viewport.invert = !viewport.invert;
        cornerstone.setViewport(element, viewport);
        Logger.log(Logger.levels.INFO, '图像反相');
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '反相失败', error);
    }
}

// 重置视图
function resetView() {
    try {
        const viewport = cornerstone.getViewport(element);
        viewport.scale = 1;
        viewport.translation = { x: 0, y: 0 };
        viewport.voi = cornerstone.getDefaultViewportForImage(element, cornerstone.getImage(element)).voi;
        viewport.rotation = 0;
        viewport.hflip = false;
        viewport.vflip = false;
        viewport.invert = false;
        cornerstone.setViewport(element, viewport);
        Logger.log(Logger.levels.INFO, '视图已重置');
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '重置视图失败', error);
    }
}

// 处理测量完成事件
function handleMeasurementCompleted(event) {
    if (event.detail.toolName === 'ZoomRegion') {
        const measurementData = event.detail.measurementData;
        const viewport = cornerstone.getViewport(element);
        
        // 获取选择区域的边界
        const { left, top, width, height } = measurementData.handles;
        
        // 计算新的缩放比例和中心点
        const imagePoint = {
            x: left + width / 2,
            y: top + height / 2
        };
        
        // 计算合适的缩放比例
        const viewportWidth = element.offsetWidth;
        const viewportHeight = element.offsetHeight;
        
        const scaleX = viewportWidth / width;
        const scaleY = viewportHeight / height;
        const scale = Math.min(scaleX, scaleY) * 0.9; // 留一些边距
        
        // 设置新的视口参数
        viewport.scale = scale;
        viewport.translation.x = (viewportWidth / 2 - imagePoint.x * scale);
        viewport.translation.y = (viewportHeight / 2 - imagePoint.y * scale);
        
        // 应用新的视口设置
        cornerstone.setViewport(element, viewport);
        
        // 清除区域选择框
        cornerstoneTools.clearToolState(element, 'ZoomRegion');
        cornerstone.updateImage(element);
        
        // 切换回之前的工具
        if (currentTool && currentTool !== 'ZoomRegion') {
            activateTool(currentTool);
        }
    }
}

// 添加键盘事件支持翻层
function initializeKeyboardControls() {
    document.addEventListener('keydown', (e) => {
        const element = document.getElementById('viewer');
        if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
            const stack = cornerstoneTools.getToolState(element, 'stack');
            if (stack && stack.data && stack.data.length) {
                const stackData = stack.data[0];
                if (e.key === 'ArrowUp' && stackData.currentImageIdIndex > 0) {
                    stackData.currentImageIdIndex--;
                } else if (e.key === 'ArrowDown' && stackData.currentImageIdIndex < stackData.imageIds.length - 1) {
                    stackData.currentImageIdIndex++;
                }
                cornerstone.loadAndCacheImage(stackData.imageIds[stackData.currentImageIdIndex])
                    .then(image => {
                        cornerstone.displayImage(element, image);
                        updateImageInfo(image); // 更新图像信息显示
                    });
            }
        }
    });
}

// 更新图像信息显示
function updateImageInfo(image) {
    const imageInfo = document.getElementById('imageInfo');
    const stack = cornerstoneTools.getToolState(element, 'stack');
    if (stack && stack.data && stack.data.length) {
        const stackData = stack.data[0];
        const currentIndex = stackData.currentImageIdIndex + 1;
        const totalImages = stackData.imageIds.length;
        imageInfo.textContent = `图像: ${currentIndex}/${totalImages}`;
    }
    // ... 其他图像信息更新
}

// 添加保存功能
function initializeSaveFunction() {
    const saveButton = document.getElementById('saveImage');
    saveButton.addEventListener('click', saveCurrentImage);
}

// 保存当前图像
async function saveCurrentImage() {
    try {
        // 获取当前图像
        const element = document.getElementById('viewer');
        const enabledElement = cornerstone.getEnabledElement(element);
        
        if (!enabledElement || !enabledElement.image) {
            throw new Error('No image to save');
        }

        // 创建一个新的 canvas
        const canvas = document.createElement('canvas');
        const context = canvas.getContext('2d');

        // 设置 canvas 尺寸为当前视图的尺寸
        canvas.width = element.offsetWidth;
        canvas.height = element.offsetHeight;

        // 将 cornerstone 元素渲染到 canvas
        cornerstone.draw(canvas, enabledElement.image);

        // 获取图像信息用于文件名
        const image = cornerstone.getImage(element);
        const imageId = image.imageId;
        const instanceNumber = image.data.string('x00200013') || '';
        const seriesNumber = image.data.string('x00200011') || '';
        const studyDate = image.data.string('x00080020') || '';
        
        // 生成文件名
        const fileName = `Image_S${seriesNumber}_I${instanceNumber}_${studyDate}.png`;

        // 将 canvas 转换为 blob
        canvas.toBlob((blob) => {
            // 创建下载链接
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            
            // 触发下载
            document.body.appendChild(a);
            a.click();
            
            // 清理
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
        }, 'image/png');

        Logger.log(Logger.levels.INFO, '图像已保存', { fileName });
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '保存图像失败', error);
        alert('保存图像失败: ' + error.message);
    }
}

// 在初始化函数中添加保存功能的初始化
function initialize() {
    // ... 其他初始化代码 ...
    initializeTools();
    initializeKeyboardControls();
    initializeSaveFunction();
}

// 初始化并加载图像
initializeViewer();
loadImages();