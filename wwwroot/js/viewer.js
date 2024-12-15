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

// 初始化 Cornerstone
function initializeViewer() {
    try {
        // 首先配置外部依赖
        cornerstoneTools.external.cornerstone = cornerstone;
        cornerstoneTools.external.Hammer = Hammer;
        cornerstoneTools.external.cornerstoneMath = cornerstoneMath;

        // 初始化 cornerstoneTools，禁用触摸功能
        cornerstoneTools.init({
            mouseEnabled: true,
            touchEnabled: false,  // 禁用触摸功能
            globalToolSyncEnabled: false,
            showSVGCursors: false
        });

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

        // 修改窗宽窗位事件监听 - 用于实时更新窗宽窗位显示
        element.addEventListener('cornerstoneimagerendered', function(e) {
            const viewport = cornerstone.getViewport(element);
            const image = cornerstone.getImage(element);
            
            // 获取现有的传输语法信息
            const windowInfo = document.getElementById('windowInfo');
            const existingText = windowInfo.innerHTML;
            const compressionInfo = existingText.split('<br>')[2] || ''; // 获取第三行（如果存在）
            
            // 更新窗宽窗位，保留传输语法信息
            windowInfo.innerHTML = [
                `窗宽: ${Math.round(viewport.voi.windowWidth)}`,
                `窗位: ${Math.round(viewport.voi.windowCenter)}`,
                compressionInfo
            ].filter(Boolean).join('<br>');
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
document.addEventListener('DOMContentLoaded', initializeViewer);

// 初始化工具
function initializeTools() {
    // 添加基础工具，但不包含滚轮工具
    cornerstoneTools.addTool(cornerstoneTools.WwwcTool);
    cornerstoneTools.addTool(cornerstoneTools.PanTool);
    cornerstoneTools.addTool(cornerstoneTools.ZoomTool);
    cornerstoneTools.addTool(cornerstoneTools.LengthTool);
    cornerstoneTools.addTool(cornerstoneTools.AngleTool);
    cornerstoneTools.addTool(cornerstoneTools.RectangleRoiTool);
    cornerstoneTools.addTool(cornerstoneTools.EllipticalRoiTool);

    // 设置默认工具状态
    cornerstoneTools.setToolActive('Wwwc', { mouseButtonMask: 1 });
    cornerstoneTools.setToolActive('Pan', { mouseButtonMask: 2 });
    cornerstoneTools.setToolActive('Zoom', { mouseButtonMask: 4 });

    // 配置工具样式
    cornerstoneTools.toolStyle.setToolWidth(2);
    cornerstoneTools.toolColors.setToolColor('rgb(255, 255, 0)');
    cornerstoneTools.toolColors.setActiveColor('rgb(0, 255, 0)');

    // 自定义滚轮事件 - 添加 passive 选项
    element.addEventListener('wheel', handleWheel, {
        passive: false  // 需要使用 preventDefault，所以设置为 false
    });

    // 禁用右键菜单
    element.addEventListener('contextmenu', e => e.preventDefault());
}

// 分离滚轮事件处理函数
function handleWheel(e) {
    if (e.shiftKey) {
        e.preventDefault();  // 只在需要的时候阻止默认行为
        // Shift + 滚轮进行缩放
        const viewport = cornerstone.getViewport(element);
        if (e.deltaY < 0) {
            viewport.scale *= 1.1;
        } else {
            viewport.scale *= 0.9;
        }
        viewport.scale = Math.max(0.3, Math.min(10, viewport.scale));
        cornerstone.setViewport(element, viewport);
    } else {
        e.preventDefault();  // 阻止默认滚动
        // 普通滚轮切换图像
        if (e.deltaY < 0) {
            displayImage(currentImageIndex - 1);
        } else {
            displayImage(currentImageIndex + 1);
        }
    }
    e.stopPropagation();
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
        'ellipse': 'EllipticalRoi',
        'invert': 'Invert'
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

    // 添加反相按钮事件监听
    const invertButton = document.getElementById('invertButton');
    if (invertButton) {
        invertButton.addEventListener('click', function(e) {
            e.preventDefault();
            e.stopPropagation();
            toggleInvert();
        });
    }
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
    } else if (this.id === 'invertButton') {
        // 切换反相按钮的激活状态
        this.classList.toggle('active');
        toggleInvert();
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
    // 获取DICOM字符集
    const specificCharacterSet = image.data.string('x00080005') || 'ISO_IR 100';
    
    // 解码DICOM文本的函数
    function decodeDicomText(value) {
        if (!value) return 'N/A';
        
        try {
            // 处理 GB18030/GB2312 编码
            if (specificCharacterSet === 'ISO_IR 58' || // GB2312
                specificCharacterSet === 'GB18030' ||
                specificCharacterSet === 'ISO_IR 192') { // UTF-8
                return new TextDecoder('gb18030').decode(new Uint8Array(value.split('').map(c => c.charCodeAt(0))));
            }
            return value;
        } catch (error) {
            console.warn('字符解码失败:', error);
            return value;
        }
    }

    // 病人信息
    document.getElementById('patientInfo').innerHTML = `
        ${decodeDicomText(image.data.string('x00100010')) || 'N/A'}<br>
        ID: ${image.data.string('x00100020') || 'N/A'}<br>
        性别: ${decodeDicomText(image.data.string('x00100040')) || 'N/A'}
    `;

    // 检查信息
    document.getElementById('studyInfo').innerHTML = `
        检查号: ${image.data.string('x00080050') || 'N/A'}<br>
        类型: ${image.data.string('x00080060') || 'N/A'}<br>
        ${formatDate(image.data.string('x00080020'))}
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

    document.getElementById('imageInfo').innerHTML = `
        序列号: ${seriesNumber}<br>
        ${numberOfFrames > 1 ? '帧号' : '图像号'}: ${numberOfFrames > 1 ? (currentFrame + 1) : instanceNumber}<br>
        ${currentImageIndex + 1}/${imageIds.length}
    `;

    // 获取传输语法并判断压缩类型
    let compressionInfo = '';
    const transferSyntaxUid = image.data.string('x00020010');

    if (transferSyntaxUid) {
        // 无损压缩格式映射
        const losslessFormats = {
            '1.2.840.10008.1.2.4.57': '无损JPEG',
            '1.2.840.10008.1.2.4.70': '无损JPEG',
            '1.2.840.10008.1.2.4.80': '无损JPEG-LS',
            '1.2.840.10008.1.2.4.90': '无损JPEG2000',
            '1.2.840.10008.1.2.4.92': '无损JPEG2000',
            '1.2.840.10008.1.2.5': '无损RLE'
        };

        // 检查是否是无损压缩格式
        const format = losslessFormats[transferSyntaxUid.trim()];
        if (format) {
            compressionInfo = format;
        }
    }

    // 更新窗宽窗位和传输语法信息
    document.getElementById('windowInfo').innerHTML = [
        `窗宽: ${Math.round(viewport.voi.windowWidth)}`,
        `窗位: ${Math.round(viewport.voi.windowCenter)}`,
        compressionInfo
    ].filter(Boolean).join('<br>');
}

// 格式日期
function formatDate(dateStr) {
    if (!dateStr) return 'N/A';
    return `${dateStr.slice(0,4)}-${dateStr.slice(4,6)}-${dateStr.slice(6,8)}`;
}

// 修改加载图像的函数
async function loadImages() {
    try {
        showLoadingIndicator();
        
        const response = await axios.get(`/api/images/${studyUid}/series/${seriesUid}/instances`);
        let instances = response.data;

        // 按实例号排序
        instances.sort((a, b) => {
            const instanceNumberA = parseInt(a.instanceNumber) || 0;
            const instanceNumberB = parseInt(b.instanceNumber) || 0;
            return instanceNumberA - instanceNumberB;
        });
        
        // 构建图像ID数组
        imageIds = [];

        // 先构建完整的 imageIds 数组
        for (const instance of instances) {
            const imageId = `wadouri:${baseUrl}/api/images/download/${instance.sopInstanceUid}?transferSyntax=jpeg`;
            try {
                // 加载第一张图像并显示
                if (imageIds.length === 0) {
                    const image = await cornerstone.loadAndCacheImage(imageId);
                    const numberOfFrames = image.data.intString('x00280008') || 1;
                    
                    if (numberOfFrames > 1) {
                        // 多帧图像
                        for (let frameIndex = 0; frameIndex < numberOfFrames; frameIndex++) {
                            imageIds.push(`${imageId}?frame=${frameIndex}`);
                        }
                    } else {
                        imageIds.push(imageId);
                    }
                    // 显示第一张图像并关闭加载提示
                    await displayImage(0);
                    hideLoadingIndicator();  // 第一张图像显示后就关闭加载提示
                } else {
                    // 后台加载其他图像
                    const image = await cornerstone.loadAndCacheImage(imageId);
                    const numberOfFrames = image.data.intString('x00280008') || 1;
                    
                    if (numberOfFrames > 1) {
                        for (let frameIndex = 0; frameIndex < numberOfFrames; frameIndex++) {
                            imageIds.push(`${imageId}?frame=${frameIndex}`);
                        }
                    } else {
                        imageIds.push(imageId);
                    }
                }
            } catch (error) {
                console.error('[Error] Failed to load image:', error);
            }
        }

        if (imageIds.length === 0) {
            throw new Error('No valid images loaded');
        }

        // 后台预加载其余图像
        imageIds.slice(1).forEach(imageId => {
            cornerstone.loadAndCacheImage(imageId).catch(() => {});
        });

    } catch (error) {
        console.error('[Error] Failed to load images:', error);
        alert('加载图像失败');
        hideLoadingIndicator();  // 确保出错时也关闭加载提示
    }
}

// 添加显示/隐藏加载指示器的函数
function showLoadingIndicator() {
    isLoading = true;
    const loadingIndicator = document.createElement('div');
    loadingIndicator.id = 'loadingIndicator';
    loadingIndicator.innerHTML = `
        <div class="loading-spinner"></div>
        <div class="loading-text">加载中...</div>
    `;
    document.getElementById('viewer').appendChild(loadingIndicator);
}

function hideLoadingIndicator() {
    isLoading = false;
    const loadingIndicator = document.getElementById('loadingIndicator');
    if (loadingIndicator) {
        loadingIndicator.remove();
    }
}

// 修改显示图像的函数，优化性能
async function displayImage(index) {
    if (index < 0) {
        index = imageIds.length - 1;
    } else if (index >= imageIds.length) {
        index = 0;
    }
    
    try {
        // 预加载下一张图像
        const nextIndex = (index + 1) % imageIds.length;
        cornerstone.loadAndCacheImage(imageIds[nextIndex]).catch(() => {});

        // 加载当前图像
        const imageId = imageIds[index];
        const image = await cornerstone.loadAndCacheImage(imageId);
        
        // 准备视口配置
        const viewport = cornerstone.getDefaultViewportForImage(element, image);
        const currentViewport = cornerstone.getViewport(element);
        
        if (currentViewport) {
            viewport.scale = currentViewport.scale;
            viewport.translation = currentViewport.translation;
            viewport.voi = currentViewport.voi;
            viewport.invert = currentViewport.invert;
        }

        // 使用 requestAnimationFrame 优化渲染
        requestAnimationFrame(() => {
            cornerstone.displayImage(element, image, viewport);
            currentImageIndex = index;
            
            // 延迟更新角落信息
            setTimeout(() => {
                updateCornerInfo(image, viewport);
            }, 0);
        });
    } catch (error) {
        console.error('显示图像失败:', error);
    }
}

// 修改播放控制函数
function togglePlay() {
    if (isPlaying) {
        pausePlay();
    } else {
        startPlay();
    }
}

// 修改播放控制函数，优化性能
function startPlay() {
    if (!isPlaying && imageIds.length > 1) {
        isPlaying = true;
        const playButton = document.getElementById('playButton');
        playButton.innerHTML = '<img src="images/tools/pause.svg" alt="暂停" width="20" height="20">';
        
        let lastTime = performance.now();
        let frameCount = 0;
        
        const animate = (currentTime) => {
            if (!isPlaying) return;

            const deltaTime = currentTime - lastTime;
            
            // 限制帧率
            if (deltaTime >= playbackSpeed) {
                frameCount++;
                if (frameCount % 2 === 0) { // 每两帧更新一次
                    let nextIndex = currentImageIndex + 1;
                    if (nextIndex >= imageIds.length) {
                        nextIndex = 0;
                    }
                    displayImage(nextIndex);
                }
                lastTime = currentTime;
            }
            
            requestAnimationFrame(animate);
        };
        
        requestAnimationFrame(animate);
    }
}

function pausePlay() {
    if (isPlaying) {
        isPlaying = false;
        const playButton = document.getElementById('playButton');
        playButton.innerHTML = '<img src="images/tools/play.svg" alt="播放" width="20" height="20">';
    }
}

// 修改反相功能
function toggleInvert() {
    const viewport = cornerstone.getViewport(element);
    viewport.invert = !viewport.invert;
    cornerstone.setViewport(element, viewport);

    // 保持反相状态
    const invertButton = document.getElementById('invertButton');
    if (invertButton) {
        invertButton.classList.toggle('active', viewport.invert);
    }
}

// 修改文件末尾的初始化代码
// 只保留一处初始化
document.addEventListener('DOMContentLoaded', () => {
    initializeViewer();
    loadImages();
});