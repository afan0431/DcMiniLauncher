; 假宿主（testhost.cpp 的 FAKE_OPENFILE 版本）代码节里的两处调用点, 字节与游戏里 FileInterface::OpenFile 的主备特征码相同:
;   主 E8 ?? ?? ?? ?? 0F B6 C8 3C 01
;   备 E8 ?? ?? ?? ?? 3C 01 0F 85 1C 04 00 00
; 两处都调用 FakeOpenFile; 只被模块扫描, 不被执行
EXTERN FakeOpenFile:PROC

.code

FakeOpenFileCallSites PROC
    call FakeOpenFile
    db 0Fh, 0B6h, 0C8h, 3Ch, 01h
    int 3
    call FakeOpenFile
    db 3Ch, 01h, 0Fh, 85h, 1Ch, 04h, 00h, 00h
    int 3
FakeOpenFileCallSites ENDP

END
