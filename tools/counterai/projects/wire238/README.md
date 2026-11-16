# CounterAI - project dem soi day dong (238 soi)

Du lieu: 12 anh bmp tiet dien bo day dong, moi anh 238 soi, nhan `<anh>_points.csv` (id,x,y).
Anh/nhan goc KHONG nam trong repo (nguon: C:\Users\TEAM\Documents\MANUAL) vi la du lieu that.

## Train (2026-11-14)
- Backend: tools/counterai/python/counterai_service.py (train), GPU RTX 5060, torch 2.14.1+cu130.
- 1500 vong, 31 giay, loss 0.415 -> 0.008. 10 anh hoc / 2 anh kiem tra (170152551, 170157312).
- Trong so: `fastnet_wire.pt` (cung thu muc), log: `train.log`.

## Ket qua
| Nguong | Lech so soi (2 anh kiem tra) | Lech (10 anh hoc) |
|---|---|---|
| 0.5-0.8 | -1 | 0 ... +0.4 |
| 0.9 | -4.5 | -2.2 |
- F1 vi tri tren 2 anh kiem tra: 0.998. Nen dung nguong 0.5-0.8, khong dung 0.9.
- Gioi han: tap kiem tra chi 2 anh, cung mot bo day nguon; chua thu tren day ngoai tap.

## Dung trong EAI
Project loai CounterAI (project.json: "projectKind": "counterai"), cong cu Dem -> o "Trong so" chon `fastnet_wire.pt`.
