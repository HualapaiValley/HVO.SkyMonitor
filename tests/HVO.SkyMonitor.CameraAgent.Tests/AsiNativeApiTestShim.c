#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct {
    char Name[64];
    int CameraID;
    long MaxHeight;
    long MaxWidth;
    int IsColorCam;
    int BayerPattern;
    int SupportedBins[16];
    int SupportedVideoFormat[8];
    double PixelSize;
    int MechanicalShutter;
    int ST4Port;
    int IsCoolerCam;
    int IsUSB3Host;
    int IsUSB3Camera;
    float ElecPerADU;
    int BitDepth;
    int IsTriggerCam;
    char Unused[16];
} ASI_CAMERA_INFO;

typedef struct {
    char Name[64];
    char Description[128];
    long MaxValue;
    long MinValue;
    long DefaultValue;
    int IsAutoSupported;
    int IsWritable;
    int ControlType;
    char Unused[32];
} ASI_CONTROL_CAPS;

typedef struct { unsigned char id[8]; } ASI_SN;

static long control_value;
static int roi_width;
static int roi_height;
static int roi_bin;
static int roi_type;
static int start_x;
static int start_y;

const char *ASIGetSDKVersion(void) { return "1.41-test"; }
int ASIGetNumOfConnectedCameras(void) { return 1; }

int ASIGetCameraProperty(ASI_CAMERA_INFO *info, int camera_index) {
    (void)camera_index;
    memset(info, 0, sizeof(*info));
    snprintf(info->Name, sizeof(info->Name), "%s", "ZWO ASI676MC");
    info->CameraID = 7;
    info->MaxHeight = 3552;
    info->MaxWidth = 3552;
    info->IsColorCam = 1;
    info->BayerPattern = 0;
    info->SupportedBins[0] = 1;
    for (int index = 0; index < 8; index++) info->SupportedVideoFormat[index] = -1;
    info->SupportedVideoFormat[0] = 2;
    info->PixelSize = 2.0;
    info->BitDepth = 12;
    return 0;
}

int ASIGetSerialNumber(int camera_id, ASI_SN *serial) {
    (void)camera_id;
    for (int index = 0; index < 8; index++) serial->id[index] = (unsigned char)index;
    return 0;
}

int ASIOpenCamera(int camera_id) { (void)camera_id; return 0; }
int ASIInitCamera(int camera_id) { (void)camera_id; return 0; }
int ASICloseCamera(int camera_id) { (void)camera_id; return 0; }

int ASIGetNumOfControls(int camera_id, int *count) {
    (void)camera_id;
    *count = 1;
    return 0;
}

int ASIGetControlCaps(int camera_id, int control_index, ASI_CONTROL_CAPS *caps) {
    (void)camera_id;
    (void)control_index;
    memset(caps, 0, sizeof(*caps));
    caps->MaxValue = 5000000123L;
    caps->MinValue = 32;
    caps->DefaultValue = 100000;
    caps->IsWritable = 1;
    caps->ControlType = 1;
    return 0;
}

int ASIGetControlValue(int camera_id, int control_type, long *value, int *automatic) {
    (void)camera_id;
    (void)control_type;
    *value = control_value;
    *automatic = 0;
    return 0;
}

int ASISetControlValue(int camera_id, int control_type, long value, int automatic) {
    (void)camera_id;
    (void)control_type;
    (void)automatic;
    control_value = value;
    return 0;
}

int ASISetROIFormat(int camera_id, int width, int height, int bin, int image_type) {
    (void)camera_id;
    roi_width = width;
    roi_height = height;
    roi_bin = bin;
    roi_type = image_type;
    return 0;
}

int ASIGetROIFormat(int camera_id, int *width, int *height, int *bin, int *image_type) {
    (void)camera_id;
    *width = roi_width;
    *height = roi_height;
    *bin = roi_bin;
    *image_type = roi_type;
    return 0;
}

int ASISetStartPos(int camera_id, int x, int y) {
    (void)camera_id;
    start_x = x;
    start_y = y;
    return 0;
}

int ASIGetStartPos(int camera_id, int *x, int *y) {
    (void)camera_id;
    *x = start_x;
    *y = start_y;
    return 0;
}

int ASIStartExposure(int camera_id, int dark) { (void)camera_id; (void)dark; return 0; }
int ASIGetExpStatus(int camera_id, int *status) { (void)camera_id; *status = 2; return 0; }
int ASIStopExposure(int camera_id) { (void)camera_id; return 0; }

int ASIGetDataAfterExp(int camera_id, unsigned char *buffer, long buffer_size) {
    (void)camera_id;
    for (long index = 0; index < buffer_size; index++) buffer[index] = (unsigned char)(index % 251);
    return 0;
}
