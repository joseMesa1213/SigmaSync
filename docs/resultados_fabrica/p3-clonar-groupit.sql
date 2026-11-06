-- P3: clona el usuario GROUPIT de la copia de Fábrica (SAINT_ADM_SYNCPRB) a la copia del CEDI (BODEGA1_SYNCPRB).
-- ESCRIBE solo en BODEGA1_SYNCPRB (guarda con DB_NAME). Producción no se toca. Cerrar antes los SAINT de prueba del CEDI.
USE BODEGA1_SYNCPRB;
IF DB_NAME() <> 'BODEGA1_SYNCPRB' BEGIN RAISERROR('No es la copia del CEDI. No se hace nada.', 16, 1); RETURN; END
IF NOT EXISTS (SELECT 1 FROM SAINT_ADM_SYNCPRB.dbo.SSUSRS WHERE CodUsua = 'GROUPIT')
BEGIN RAISERROR('GROUPIT no existe en SAINT_ADM_SYNCPRB.', 16, 1); RETURN; END

-- Antes: usuarios y configuraciones que existen en el CEDI
SELECT CodUsua, Descrip, Profile FROM SSUSRS;
SELECT CodParm, COUNT(DISTINCT Modulo) modulos, COUNT(*) parametros FROM SSPARM GROUP BY CodParm ORDER BY CodParm;

BEGIN TRAN;
IF NOT EXISTS (SELECT 1 FROM SSUSRS WHERE CodUsua = 'GROUPIT')
    INSERT SSUSRS (CodUsua, Descrip, Email, Movil, IdUserNot, Profile, CodVend, UsrDta1, UsrDta2, UsrDta3, UsrDta4, UsrDta5, SData1, SData2, SData3)
    SELECT CodUsua, Descrip, Email, Movil, IdUserNot, Profile, CodVend, UsrDta1, UsrDta2, UsrDta3, UsrDta4, UsrDta5, SData1, SData2, SData3
    FROM SAINT_ADM_SYNCPRB.dbo.SSUSRS WHERE CodUsua = 'GROUPIT';

DELETE FROM SSPARD WHERE CodParm = '_U_GROUPIT';
DELETE FROM SSPARM WHERE CodParm = '_U_GROUPIT';
INSERT SSPARM (CodParm, Modulo, Parametro, Activo, Clave, Habilitado, SSFld)
SELECT CodParm, Modulo, Parametro, Activo, Clave, Habilitado, SSFld FROM SAINT_ADM_SYNCPRB.dbo.SSPARM WHERE CodParm = '_U_GROUPIT';
INSERT SSPARD (CodParm, Modulo, Parametro, NoData, TipoData, IData, SData, FData, SSFld)
SELECT CodParm, Modulo, Parametro, NoData, TipoData, IData, SData, FData, SSFld FROM SAINT_ADM_SYNCPRB.dbo.SSPARD WHERE CodParm = '_U_GROUPIT';

-- Después: GROUPIT debe aparecer con los mismos módulos que en Fábrica
SELECT CodUsua, Descrip, Profile FROM SSUSRS WHERE CodUsua = 'GROUPIT';
SELECT 'cedi' donde, Modulo, COUNT(*) n FROM SSPARM WHERE CodParm = '_U_GROUPIT' GROUP BY Modulo
UNION ALL
SELECT 'fabrica', Modulo, COUNT(*) FROM SAINT_ADM_SYNCPRB.dbo.SSPARM WHERE CodParm = '_U_GROUPIT' GROUP BY Modulo
ORDER BY 2, 1;
COMMIT;
