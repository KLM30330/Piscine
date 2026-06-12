# Piscine
Automatisation de la piscine

le système se compose d'un Raspberry qui pilote la domotique de la maison via home assistant et d'un deuxième Raspberry qui pilote la piscine. la pompe de piscine a un débit de 17m3/h et la piscine fait 38m3.

Ce qui raccordé sur le Raspberry de la Piscine :
I2C :	
	* Atlas scientific :
		- module PH EZO-pH
		- module REDOX EZO-ORP
		- module température EZO™ RTD
		- pompe doseuse péristaltique EZO-PMP
	* PCF8574
		- relai de commande de l'électrolyseur en mode on/off
	* ecran LCD1602 16x2

One wire :
	* sonde de température DS18B20 pour surveiller la température de la pompe de piscine

Gpio :
	* 4 boutons poussoir en face avant

USB :
	* Interface USB RS485
		- variateur Wk600-d alimente la pompe, le variateur est piloté par modbus	

Le Raspberry piscine gère en automatique :
* la régulation du Ph
* la filtration de la piscine régulée par :
	- la température de l'eau
	- la valeur de redox.

Actions manuelles depuis le Raspberry piscine : 
* le 1er bouton poussoir (gpio5) lance l'affichage LCD pendant 30s qui indique la température, le PH et redox
* le 2eme bouton poussoir (gpio6) lance l'amorçage de la pompe doseuse avec affichage LCD "amorçage ph- " + chrono
* le 3eme bouton poussoir (gpio13) met en pause la filtration et affiche sur le LCD pendant 30s "Filtration en pause"
* le 4eme bouton poussoir (gpio19) relance la filtration en mode auto et affiche sur le LCD pendant 30s "reprise filtration"

Informations renvoyées à home assistant :
	- température de l'eau
	- valeur de ph
	- valeur redox
	- fonctionnement de la pompe péristaltique
	- pompe en fonctionnement	
	- fréquence de fonctionnement de la pompe
	- intensité consommée par la pompe
	- puissance consommée par la pompe
	- température de la pompe

Commande depuis home assistant:
	- mode auto
	- mode forcé
	- mode boost
	- arrêt filtration
	- valeur de fréquence de fonctionnement de la pompe
	- amorçage pompe péristaltique
	- étalonnage de la sonde de ph

Automations home assistant :
	- si filtration auto => Notification journalière temps de fonctionnement de la pompe valeur Ph, redox, température + analyse
	- si redox ou ph trop bas ou trop haut => alarme
  

