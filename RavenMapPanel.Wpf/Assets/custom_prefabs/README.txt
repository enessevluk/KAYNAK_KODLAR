RAVEN CUSTOM PREFAB KAYNAK KLASORU
=================================

BU KLASOR TEK GERCEK PREFAB KAYNAGIDIR.

Prefab dosyalarini SERVER\Content icine elle koyma.
RAVEN.bat > 3 SERVER BUILD sirasinda bu klasor SERVER\Content\Prefabs
alanina birebir senkronize edilir.

Klasor yapisi:

  STANDARD\
    outpost\
    bandit_camp\
    stables_a\
    stables_b\
    fishing_village_a\
    fishing_village_b\
    fishing_village_c\

  PREMIUM\
    outpost\
    bandit_camp\
    stables_a\
    stables_b\
    fishing_village_a\
    fishing_village_b\
    fishing_village_c\

PLAN MANTIGI
------------
STANDARD lisans:
  Sadece STANDARD klasorundeki prefablar.

PREMIUM lisans:
  STANDARD + PREMIUM klasorlerindeki prefablar.

Ornek:

  STANDARD\outpost\outpost_standard.prefab.map
  PREMIUM\outpost\outpost_premium.prefab.map

Standard kullanici:
  Vanilla
  Outpost Standard

Premium kullanici:
  Vanilla
  Outpost Standard
  Outpost Premium

DOSYA ADI
---------
Dosya adinin .map ile bitmesi yeterlidir.
Panelde daha duzgun gorunmesi icin .prefab.map kullanilmasi onerilir.

Ornek:
  outpost_standard.prefab.map
  outpost_premium.prefab.map
  outpost_military.prefab.map

ESKI YAPIDAN GECIS
------------------
Eski surumde bu klasorun kokunde bulunan:
  rustmaps_outpost_template.map
  rustmaps_bandit_camp_template.map
  rustmaps_stables_a_template.map
  rustmaps_stables_b_template.map
  rustmaps_fishing_village_a_template.map
  rustmaps_fishing_village_b_template.map
  rustmaps_fishing_village_c_template.map

dosyalari RAVEN.bat ilk prefab islemi / SERVER BUILD sirasinda otomatik olarak
STANDARD altindaki dogru klasore tasinir ve yeni isimle kaydedilir.

ONEMLI
------
SERVER\Content\Prefabs uretilmis kopyadir.
Bir prefabi kalici silmek veya degistirmek icin BURADAKI kaynak dosyayi
sil / degistir ve sonra SERVER BUILD yap.
