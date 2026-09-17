RAVEN PREFAB SUNUCU ALANI
=========================

Bu Raven.Server proje klasoru owner prefablarinin ana kaynagi DEGILDIR.

Owner prefablarinin tek kaynak klasoru:
  RavenMapPanel.Wpf\Assets\custom_prefabs\STANDARD\...
  RavenMapPanel.Wpf\Assets\custom_prefabs\PREMIUM\...

RAVEN.bat > SERVER BUILD bu kaynaklari:
  ..\SERVER\Content\Prefabs\STANDARD\...
  ..\SERVER\Content\Prefabs\PREMIUM\...

alanina senkronize eder.

Calisan Raven Server lisans ve plan kontrolunden sonra yalnizca yetkili
prefablari /api/prefabs/file endpoint'i ile sunar.

Plan mantigi:
  STANDARD -> yalniz STANDARD
  PREMIUM  -> STANDARD + PREMIUM

Desteklenen monument klasorleri:
  outpost, bandit_camp, stables_a, stables_b,
  fishing_village_a, fishing_village_b, fishing_village_c
