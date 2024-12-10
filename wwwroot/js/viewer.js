console.log('viewer.js loaded');

let currentImageIndex = 0;
let imageIds = [];
let currentTool = 'Wwwc';
let isPlaying = false;
let playInterval = null;
let playbackSpeed = 200;

const element = document.getElementById('viewer');
console.log('viewer element:', element);

const urlParams = new URLSearchParams(window.location.search);
const studyUid = urlParams.get('studyUid');
const seriesUid = urlParams.get('seriesUid');
const baseUrl = window.location.origin;

const CONFIG = {
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
        
        // 使用 requestAnimationFrame 确保状态更新更流畅
        requestAnimationFrame(() => {
            this.statusIndicator.textContent = message;
            this.statusIndicator.style.opacity = '1';
        });
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

// 窗位预设配置
const WINDOW_PRESETS = {
    default: { description: '默认', ww: null, wc: null },  // null 表示使用图像默认值
    // CT 脑部窗位
    brain: { description: '脑窗', ww: 80, wc: 40 },
    brainSoft: { description: '脑软组织', ww: 120, wc: 35 },
    subdural: { description: '硬膜下', ww: 350, wc: 90 },
    stroke: { description: '卒中窗', ww: 40, wc: 40 },
    
    // CT 胸部窗位
    lung: { description: '肺窗', ww: 1500, wc: -600 },
    mediastinum: { description: '纵隔窗', ww: 350, wc: 50 },
    chest: { description: '胸窗', ww: 400, wc: 40 },
    
    // CT 骨骼和软组织
    bone: { description: '骨窗', ww: 2500, wc: 480 },
    softTissue: { description: '软组织', ww: 400, wc: 40 },
    liver: { description: '肝窗', ww: 150, wc: 30 },
    
    // 特殊窗位
    angio: { description: '血管窗', ww: 600, wc: 300 },
    spine: { description: '脊柱窗', ww: 300, wc: 40 },
    temporal: { description: '颞骨窗', ww: 4000, wc: 700 }
};

// 添加传输语法名称转换函数
function getTransferSyntaxName(transferSyntax) {
    const syntaxMap = {
        '1.2.840.10008.1.2': 'Implicit VR LE',
        '1.2.840.10008.1.2.1': 'Explicit VR LE',
        '1.2.840.10008.1.2.2': 'Explicit VR BE',
        '1.2.840.10008.1.2.4.50': 'JPEG Baseline',
        '1.2.840.10008.1.2.4.51': 'JPEG Extended',
        '1.2.840.10008.1.2.4.57': 'JPEG Lossless',
        '1.2.840.10008.1.2.4.70': 'JPEG Lossless',
        '1.2.840.10008.1.2.4.80': 'JPEG-LS',
        '1.2.840.10008.1.2.4.81': 'JPEG-LS',
        '1.2.840.10008.1.2.4.90': 'JPEG 2000',
        '1.2.840.10008.1.2.4.91': 'JPEG 2000',
        '1.2.840.10008.1.2.5': 'RLE Lossless'
    };
    
    return syntaxMap[transferSyntax] || transferSyntax;
}

// 初始化 Cornerstone
function initializeViewer() {
    try {
        // 配置 cornerstone
        cornerstoneTools.external.cornerstone = cornerstone;
        cornerstoneTools.external.Hammer = Hammer;
        
        // 配置 WADO 图像加载器
        cornerstoneWADOImageLoader.external.cornerstone = cornerstone;
        cornerstoneWADOImageLoader.external.dicomParser = dicomParser;
        
        // 先启用 element
        cornerstone.enable(element);
        
        // 获取 canvas 并设置属性
        const canvas = element.querySelector('canvas');
        if (canvas) {
            // 重新创建带有 willReadFrequently 的 context
            canvas.width = canvas.width;  // 重置 canvas
            const ctx = canvas.getContext('2d', { 
                willReadFrequently: true,
                preserveDrawingBuffer: true
            });
            // 保存 context 引用
            canvas._cornerstone_context = ctx;
        }
        
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

        // 初始化工具
        initializeTools();
        
        // 设置工具栏事件
        setupToolbar();

        // 添加图像渲染事件监听
        cornerstone.events.addEventListener('cornerstoneimagerendered', onImageRendered);
        
        // 添加窗宽窗位实时更新事件
        element.addEventListener('cornerstoneimagerendered', function(e) {
            const viewport = cornerstone.getViewport(element);
            const image = cornerstone.getImage(element);
            const transferSyntax = image.data.string('x00020010') || 'N/A';
            const transferSyntaxName = getTransferSyntaxName(transferSyntax);
            
            // 确保窗位信息显示是最新的
            document.getElementById('windowInfo').innerHTML = `
                窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
                窗位: ${Math.round(viewport.voi.windowCenter)}<br>
                ${transferSyntaxName}
            `;
        });

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

        // 修改播放控制事件监听
        const playButton = document.getElementById('playButton');
        if (playButton) {
            // 移除可能存在的旧事件监听器
            const newPlayButton = playButton.cloneNode(true);
            playButton.parentNode.replaceChild(newPlayButton, playButton);
            
            // 添加新的事件监听器
            newPlayButton.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                console.log('Play button clicked');
                togglePlay();
            });
        }

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

// 修始化
document.addEventListener('DOMContentLoaded', () => {
    initializeViewer();
    loadImages();
});

// 优化初始化工具函数
function initializeTools() {
    cornerstoneTools.init();

    // 初始化所有工具为被动状态
    const toolsToInitialize = [
        'Length',
        'Angle',
        'RectangleRoi',
        'EllipticalRoi',
        'Probe',
        'Wwwc',
        'Pan',
        'Zoom',
        'StackScroll'
    ];
    
    toolsToInitialize.forEach(toolName => {
        cornerstoneTools.setToolPassive(toolName);
    });

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

    // 设置默认工具状态
    currentTool = 'Wwwc';

    // 初始化探针工具
    try {
        // 重新添加探针工具
        cornerstoneTools.addTool(cornerstoneTools.ProbeTool, {
            configuration: {
                shadow: true,
                drawHandles: true,
                handleRadius: 2,
                fontSize: 14,
                textBox: true,
                formatCallbackText: (data) => {
                    if (!data || !data.currentPoints || !data.currentPoints.image) {
                        return '';
                    }
                    return `HU: ${data.currentPoints.image.value}`;
                }
            }
        });
    } catch (error) {
        console.error('Failed to initialize probe tool:', error);
    }

    // 配置工具样式
    cornerstoneTools.toolStyle.setToolWidth(2);
    cornerstoneTools.toolColors.setToolColor(CONFIG.TOOL_COLORS.DEFAULT);
    cornerstoneTools.toolColors.setActiveColor(CONFIG.TOOL_COLORS.ACTIVE);

    // 设置默认工具状态
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });

    // 确保调窗按钮被激活
    const wwwcButton = document.querySelector('[data-tool="Wwwc"]');
    if (wwwcButton) {
        wwwcButton.classList.add('active');
        // 移除其他按钮的激活状态
        document.querySelectorAll('.tool-button').forEach(btn => {
            if (btn !== wwwcButton) {
                btn.classList.remove('active');
            }
        });
    }

    // 初始化翻层工具
    cornerstoneTools.addTool(cornerstoneTools.StackScrollTool);
    cornerstoneTools.setToolActive('StackScroll', { mouseButtonMask: 1 });

    setupToolEvents();
}

// 修改禁用工具函数
function disableAllTools() {
    // 禁用所有工具按钮的视觉效果，除了播放按钮
    document.querySelectorAll('.tool-button').forEach(btn => {
        if (btn.id !== 'playButton') {
            btn.classList.add('disabled');
            btn.style.opacity = '0.5';
            btn.style.pointerEvents = 'none';
        }
    });
    
    // 禁用具体的 Cornerstone 工具
    const toolsToDisable = [
        'StackScroll',
        'Probe',
        'Wwwc',
        'Pan',
        'Zoom',
        'Length',
        'Angle',
        'RectangleRoi',
        'EllipticalRoi'
    ];

    toolsToDisable.forEach(toolName => {
        cornerstoneTools.setToolDisabled(toolName);
    });
    
    // 禁用鼠标滚轮
    element.removeEventListener('wheel', handleMouseWheel);
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
    
    // 添加平滑
    requestAnimationFrame(() => {
        cornerstone.setViewport(element, viewport);
    });
}

// 分离图像切换逻辑
function handleImageScroll(e) {
    // 如果没有图像或只有一张图像，直接返回
    if (!imageIds.length || imageIds.length === 1) return;

    const direction = e.deltaY < 0 ? -1 : 1;
    let nextIndex = currentImageIndex + direction;
    
    // 循环滚动
    if (nextIndex < 0) {
        nextIndex = imageIds.length - 1;
    } else if (nextIndex >= imageIds.length) {
        nextIndex = 0;
    }
    
    displayImage(nextIndex);
}

// 激活工具
function activateTool(toolName) {
    const tool = TOOL_MAP[toolName];
    if (!tool) return;

    try {
        // 将所有工具设置为被动状态
        const toolsToPassive = [
            'Length',
            'Angle',
            'RectangleRoi',
            'EllipticalRoi',
            'Probe',
            'Wwwc',
            'Pan',
            'Zoom'
        ];
        
        toolsToPassive.forEach(name => {
            cornerstoneTools.setToolEnabled(name);
        });

        // 更新当前工具
        currentTool = toolName;

        // 更新按钮状态
        document.querySelectorAll('.tool-button').forEach(btn => {
            const btnToolName = btn.dataset.tool;
            if (btnToolName === toolName) {
                btn.classList.add('active');
            } else {
                btn.classList.remove('active');
            }
        });

        // 特殊处理调窗工具
        if (toolName === 'Wwwc') {
            cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
            return;
        }

        // 激活选中的工具
        if (toolName === 'Zoom') {
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 1 });
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
        } else if (toolName === 'Pan') {
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 1 });
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 2 });
        } else {
            cornerstoneTools.setToolActive(tool.name, { mouseButtonMask: 1 });
            cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
            cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
        }

        Logger.log(Logger.levels.INFO, `工具已激活: ${tool.label}`);
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '激活工具失败:', error);
    }
}

// 修改工具栏设置函数
function setupToolbar() {
    // 先移除所有已有的件监听器
    const toolButtons = document.querySelectorAll('.tool-button');
    toolButtons.forEach(button => {
        const newButton = button.cloneNode(true);
        button.parentNode.replaceChild(newButton, button);
    });

    // 重新添加工按钮的点击事件
    document.querySelectorAll('.tool-button').forEach(button => {
        button.addEventListener('click', handleToolButtonClick);
    });

    // 添加窗位预设菜单事件
    const presetsButton = document.getElementById('windowPresets');
    const presetsMenu = document.getElementById('windowPresetsMenu');
    
    if (presetsButton && presetsMenu) {
        presetsButton.addEventListener('click', function(e) {
            e.preventDefault();
            e.stopPropagation();
            
            // 关闭其他可能打开的菜单
            document.querySelectorAll('.window-presets-menu').forEach(menu => {
                if (menu !== presetsMenu) {
                    menu.classList.remove('show');
                }
            });
            
            presetsMenu.classList.toggle('show');
            
            // 更新其他按钮状态
            document.querySelectorAll('.tool-button').forEach(btn => {
                if (btn !== presetsButton) {
                    btn.classList.remove('active');
                }
            });
            presetsButton.classList.toggle('active');
        });
        
        // 击预设按钮
        document.querySelectorAll('.preset-button').forEach(button => {
            button.addEventListener('click', function(e) {
                e.preventDefault();
                e.stopPropagation();
                handleWindowPreset(e);
                presetsMenu.classList.remove('show');
                presetsButton.classList.remove('active');
            });
        });
        
        // 击其他地方关闭菜单
        document.addEventListener('click', function(e) {
            if (!presetsMenu.contains(e.target) && !presetsButton.contains(e.target)) {
                presetsMenu.classList.remove('show');
                presetsButton.classList.remove('active');
            }
        });
    }
}

// 修改工具按钮点击处理函数
function handleToolButtonClick(e) {
    e.preventDefault();
    e.stopPropagation();

    // 处理特殊按钮
    if (this.id) {
        switch (this.id) {
            case 'resetView':
                resetView();
                return;
            case 'clearAnnotations':
                clearAnnotations();
                return;
            case 'showDicomTags':
                DicomTagsViewer.show();
                return;
            case 'playButton':
                togglePlay();
                return;
        }
    }

    const toolName = this.dataset.tool;
    if (!toolName) {
        return;
    }

    // 理特殊工具
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
        case 'Stack':
            cornerstoneTools.setToolActive('StackScroll', { mouseButtonMask: 1 });
            activateToolButton(this);
            return;
        case 'Probe':
            try {
                // 先清除现有的探针标注
                cornerstoneTools.clearToolState(element, 'Probe');
                // 激活探针工具
                cornerstoneTools.setToolActive('Probe', { mouseButtonMask: 1 });
                // 保持平移和缩放可用
                cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
                cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
                activateToolButton(this);
                // 设置当前工具
                currentTool = 'Probe';
            } catch (error) {
                console.error('Failed to activate probe tool:', error);
            }
            return;
    }

    // 处理常规工具
    activateToolButton(this);
    activateTool(toolName);
}

// 添加工具按钮激活函数
function activateToolButton(button) {
    document.querySelectorAll('.tool-button').forEach(btn => {
        btn.classList.remove('active');
    });
    button.classList.add('active');
}

// 清除标注
function clearAnnotations() {
    try {
        // 清除所有测量工具的标注
        const toolsToClean = [
            'Length',
            'Angle',
            'RectangleRoi',
            'EllipticalRoi',
            'Probe'
        ];
        
        // 遍历清除每个工具的标注
        toolsToClean.forEach(toolName => {
            try {
                cornerstoneTools.clearToolState(element, toolName);
            } catch (err) {
                console.warn(`清除工具 ${toolName} 状态失败:`, err);
            }
        });
        
        cornerstone.updateImage(element);
        Logger.log(Logger.levels.INFO, '所有标注已清除');
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '清除标注失败', error);
    }
}

// 图像渲染事件处理
function onImageRendered(e) {
    const viewport = cornerstone.getViewport(element);
    const image = cornerstone.getImage(element);
    
    // 确保窗位值被正确应用
    if (viewport.voi.windowWidth !== undefined && viewport.voi.windowCenter !== undefined) {
        cornerstone.setViewport(element, viewport);
        
        // 立即更新窗位信息显示
        document.getElementById('windowInfo').innerHTML = `
            窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
            窗位: ${Math.round(viewport.voi.windowCenter)}<br>
            ${getCurrentPresetName(viewport)}
        `;
    }
    
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

    // 获取输语法并转换为可读格式
    const transferSyntax = image.data.string('x00020010') || 'N/A';
    const transferSyntaxName = getTransferSyntaxName(transferSyntax);

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

    // 如果是多帧图像，显示帧信息否显示图像号
    document.getElementById('imageInfo').innerHTML = `
        序列号: ${seriesNumber}<br>
        ${numberOfFrames > 1 ? '帧号' : '图像号'}: ${numberOfFrames > 1 ? (currentFrame + 1) : instanceNumber}<br>
        ${currentImageIndex + 1}/${imageIds.length}
    `;

    document.getElementById('windowInfo').innerHTML = `
        窗宽: ${Math.round(viewport.voi.windowWidth)}<br>
        窗位: ${Math.round(viewport.voi.windowCenter)}<br>
        ${transferSyntaxName}
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
        
        // 后台载其图像
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
        let loadedCount = 1; // 从1开始，因为第一张已经加载
        
        for (let i = 1; i < imageCache.instances.length; i++) {
            const instance = imageCache.instances[i];
            const imageId = `wadouri:${baseUrl}/api/images/download/${instance.sopInstanceUid}?transferSyntax=jpeg`;
            
            if (!imageCache.loaded.has(imageId) && !imageCache.loading.has(imageId)) {
                imageCache.loading.add(imageId);
                
                const loadPromise = (async () => {
                    try {
                        await cornerstone.loadAndCacheImage(imageId);
                        loadedCount++;
                        
                        // 实时更新加载进度
                        const percentage = Math.round((loadedCount / imageCache.totalCount) * 100);
                        LoadingManager.showStatus(`加载中: ${loadedCount}/${imageCache.totalCount} (${percentage}%)`);
                        
                        imageCache.loaded.add(imageId);
                        handleImageLoaded(await cornerstone.loadImage(imageId), imageId);
                        
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
        
    } catch (error) {
        Logger.log(Logger.levels.ERROR, 'Failed to load remaining images', error);
    } finally {
        // 确保在所有图像加载完成后隐藏状态
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

    // 如果是多帧图像，置堆栈状态
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
        // 不记录警告，因为已经在 handleImageScroll 中处理了边界情况
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
    console.log('Toggle play clicked, current state:', isPlaying);
    
    // 如果只有一张图片，直接返回
    if (imageIds.length <= 1) {
        Logger.log(Logger.levels.INFO, '只有一张图片，无法播放');
        return;
    }
    
    // 先处理所有工具按钮的状态
    document.querySelectorAll('.tool-button').forEach(btn => {
        btn.classList.remove('active');
    });
    
    // 确保播放按钮保持选中状态
    const playButton = document.getElementById('playButton');
    if (playButton) {
        playButton.classList.add('active');
    }
    
    if (isPlaying) {
        pausePlay();
        enableAllTools(); // 暂停时启用所有工具
    } else {
        startPlay();
        disableAllTools(); // 播放时禁用其他工具
    }
}

// 添加禁用工具函数
function disableAllTools() {
    // 禁用所有工具按钮的视觉效果，除了播放按钮
    document.querySelectorAll('.tool-button').forEach(btn => {
        if (btn.id !== 'playButton') {
            btn.classList.add('disabled');
            btn.style.opacity = '0.5';
            btn.style.pointerEvents = 'none';
        }
    });
    
    // 禁用具体的 Cornerstone 工具
    const toolsToDisable = [
        'StackScroll',
        'Probe',
        'Wwwc',
        'Pan',
        'Zoom',
        'Length',
        'Angle',
        'RectangleRoi',
        'EllipticalRoi'
    ];

    toolsToDisable.forEach(toolName => {
        cornerstoneTools.setToolDisabled(toolName);
    });
    
    // 禁用鼠标滚轮
    element.removeEventListener('wheel', handleMouseWheel);
}

// 添加启用工具函数
function enableAllTools() {
    // 启用所有工具按钮
    document.querySelectorAll('.tool-button').forEach(btn => {
        btn.classList.remove('disabled');
        btn.style.opacity = '';
        btn.style.pointerEvents = '';
    });
    
    // 重新启用默认工具
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });
    
    // 重新绑定鼠标滚轮事件
    element.addEventListener('wheel', handleMouseWheel);
    
    // 重新激活之前的工具
    if (currentTool && currentTool !== 'play') {
        activateTool(currentTool);
    }
}

function startPlay() {
    // 如果只有一张图片，直接返回
    if (imageIds.length <= 1) {
        return;
    }
    
    console.log('Starting play, imageIds length:', imageIds.length);
    if (!isPlaying) {
        isPlaying = true;
        const playButton = document.getElementById('playButton');
        if (playButton) {
            playButton.innerHTML = '<img src="images/tools/pause.svg" alt="暂停" width="20" height="20">';
            playButton.classList.add('active');
        }
        
        // 清除可能存在的旧定时器
        if (playInterval) {
            clearInterval(playInterval);
        }

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
    console.log('Pausing play');
    if (isPlaying) {
        isPlaying = false;
        const playButton = document.getElementById('playButton');
        if (playButton) {
            playButton.innerHTML = '<img src="images/tools/play.svg" alt="播放" width="20" height="20">';
            playButton.classList.remove('active'); // 暂停时移除激活状态
        }
        
        if (playInterval) {
            clearInterval(playInterval);
            playInterval = null;
        }
    }
}

// 在图像加载完成后，如果只有一张图片，禁用播放按钮
function updatePlayButtonState() {
    const playButton = document.getElementById('playButton');
    if (playButton) {
        if (imageIds.length <= 1) {
            playButton.disabled = true;
            playButton.style.opacity = '0.5';
            playButton.style.cursor = 'not-allowed';
        } else {
            playButton.disabled = false;
            playButton.style.opacity = '';
            playButton.style.cursor = 'pointer';
        }
    }
}

// 在加载图像后调用更新播放按钮状态
async function loadAndDisplayFirstImage() {
    try {
        const firstInstance = imageCache.instances[0];
        Logger.log(Logger.levels.INFO, 'Loading first instance', firstInstance);
        
        if (!firstInstance?.sopInstanceUid) {
            throw new Error('Invalid first instance data');
        }

        // 添加 transferSyntax=jpeg 参数到 URL
        const imageId = `wadouri:${baseUrl}/api/images/download/${firstInstance.sopInstanceUid}?transferSyntax=jpeg`;
        Logger.log(Logger.levels.INFO, 'Loading first image', { imageId });
        
        // 加载第一张图像
        const image = await cornerstone.loadAndCacheImage(imageId);
        
        // 处理多帧图像
        const numberOfFrames = image.data.intString('x00280008') || 1;
        
        // 重置 imageIds 数组
        imageIds = [];
        
        if (numberOfFrames > 1) {
            // 如果是多帧图像，添加所有
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
        updatePlayButtonState(); // 添加这行
        
        // 确保调窗工具是激活状态
        cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
        const wwwcButton = document.querySelector('[data-tool="Wwwc"]');
        if (wwwcButton) {
            wwwcButton.classList.add('active');
        }

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
        const element = document.getElementById('viewer');
        const image = cornerstone.getImage(element);
        // 获取图像的视口设置
        const defaultViewport = cornerstone.getDefaultViewportForImage(element, image);
        
        // 获取当前视口并重置所有变换
        const viewport = cornerstone.getViewport(element);
        viewport.scale = defaultViewport.scale;
        viewport.translation = defaultViewport.translation;
        viewport.voi = defaultViewport.voi;
        viewport.rotation = 0;
        viewport.hflip = false;
        viewport.vflip = false;
        viewport.invert = false;
        
        // 应用重置后的视口
        cornerstone.setViewport(element, viewport);
        
        // 重置工具状态
        cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
        
        // 更新所有按钮状态
        document.querySelectorAll('.tool-button').forEach(btn => {
            if (btn.dataset.tool === 'Wwwc') {
                btn.classList.add('active');
            } else {
                btn.classList.remove('active');
            }
        });
        
        // 更新当前工具
        currentTool = 'Wwwc';
        
        Logger.log(Logger.levels.INFO, '视图已重置');
    } catch (error) {
        Logger.log(Logger.levels.ERROR, '重置视图失败', error);
    }
}

// 处理测量完成件
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
        
        // 应用的视口设置
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

// 在初始化函数中添加保存功能的初始化
function initialize() {
    // ... 其他初始化码 ...
    initializeTools();
    initializeKeyboardControls();
}

// 初始化并加载图像
initializeViewer();
loadImages();

// 处理窗位预设
function handleWindowPreset(e) {
    const presetName = e.currentTarget.dataset.preset;
    const preset = WINDOW_PRESETS[presetName];
    
    if (!preset) return;

    const element = document.getElementById('viewer');
    const viewport = cornerstone.getViewport(element);
    const image = cornerstone.getImage(element);

    // 设置窗位值
    if (preset.ww === null || preset.wc === null) {
        viewport.voi.windowWidth = image.windowWidth || 400;
        viewport.voi.windowCenter = image.windowCenter || 40;
    } else {
        viewport.voi.windowWidth = preset.ww;
        viewport.voi.windowCenter = preset.wc;
    }

    // 强制应用新的窗位值
    cornerstone.setViewport(element, viewport);
    
    // 强制刷新图像显示
    cornerstone.updateImage(element, true);
    
    // 确保调窗工具处于激活状态
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    
    // 更新工具按钮状态
    document.querySelectorAll('.tool-button').forEach(btn => {
        if (btn.dataset.tool === 'Wwwc') {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }
    });

    // 关闭预设菜单
    const presetsMenu = document.getElementById('windowPresetsMenu');
    if (presetsMenu) {
        presetsMenu.classList.remove('show');
    }
    
    // 取消预设按钮的激活状态
    const presetsButton = document.getElementById('windowPresets');
    if (presetsButton) {
        presetsButton.classList.remove('active');
    }
}

// 获取当前窗位对应的预设名称
function getCurrentPresetName(viewport) {
    const ww = Math.round(viewport.voi.windowWidth);
    const wc = Math.round(viewport.voi.windowCenter);
    
    for (const [name, preset] of Object.entries(WINDOW_PRESETS)) {
        if (preset.ww === ww && preset.wc === wc) {
            return `<br>预设: ${preset.description}`;
        }
    }
    return '';
}